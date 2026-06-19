using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BabelQueue;

namespace BabelQueue.Tracing;

/// <summary>
/// Optional OpenTelemetry tracing for a babelqueue producer or consumer (ADR-0025) — the .NET
/// mirror of the Go <c>otel</c>, Python <c>babelqueue.otel</c> and Node <c>@babelqueue/core/otel</c>
/// helpers. It emits a CONSUMER <see cref="Activity"/> per handled message and a PRODUCER activity
/// per publish, correlating them across every hop and SDK through the envelope's <c>trace_id</c> —
/// a UUID, which maps 1:1 to a 16-byte <see cref="ActivityTraceId"/>.
/// </summary>
/// <remarks>
/// <para>
/// It is built on <see cref="System.Diagnostics.ActivitySource"/> — the BCL primitive that the
/// OpenTelemetry .NET SDK consumes — so the core takes <b>no</b> OpenTelemetry package dependency.
/// To export, a consumer wires an OpenTelemetry <c>TracerProvider</c> that listens to the source
/// named <see cref="ActivitySourceName"/> (e.g. <c>builder.AddSource("BabelQueue")</c>); with no
/// listener the helpers are nearly free and emit nothing. The wire envelope is untouched (GR-1).
/// </para>
/// <para>
/// Honest limit: every hop that shares a <c>trace_id</c> shares one trace, but exact cross-hop
/// <i>span</i> parent-child linkage (propagating a W3C <c>traceparent</c> as a transport header)
/// is a documented follow-up; this maps <c>trace_id</c> to the trace id only.
/// </para>
/// </remarks>
public static class Telemetry
{
    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource"/> these helpers emit on.
    /// Add it to an OpenTelemetry <c>TracerProvider</c> (<c>AddSource("BabelQueue")</c>) to export.
    /// </summary>
    public const string ActivitySourceName = "BabelQueue";

    private const string System = "babelqueue";
    private const string ZeroTraceId = "00000000000000000000000000000000";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    /// <summary>
    /// Maps an envelope <c>trace_id</c> to a deterministic <see cref="ActivityTraceId"/>: a UUID
    /// maps to its 16 hex bytes; any other string is hashed (SHA-256, first 16 bytes). The inverse
    /// of <see cref="UuidOf"/> for the UUID case.
    /// </summary>
    public static ActivityTraceId TraceIdOf(string traceId)
    {
        string hex = (traceId ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (hex.Length == 32 && IsHex(hex) && hex != ZeroTraceId)
        {
            return ActivityTraceId.CreateFromString(hex.AsSpan());
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(traceId ?? string.Empty));
        return ActivityTraceId.CreateFromBytes(digest.AsSpan(0, 16));
    }

    /// <summary>
    /// Formats an <see cref="ActivityTraceId"/> as a canonical UUID string — the form a producer
    /// stamps into the message's <c>trace_id</c> so a consumer can recover the same trace id via
    /// <see cref="TraceIdOf"/>.
    /// </summary>
    public static string UuidOf(ActivityTraceId traceId)
    {
        string h = traceId.ToHexString();
        return string.Create(CultureInfo.InvariantCulture, $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..32]}");
    }

    /// <summary>
    /// Returns <paramref name="handler"/> wrapped to emit a CONSUMER activity <c>process &lt;urn&gt;</c>
    /// in the trace derived from the envelope's <c>trace_id</c>, recording the handler's error/status.
    /// The handler still receives the full <see cref="Envelope"/>; a throw is recorded and re-thrown so
    /// the runtime's retry / dead-letter path still applies.
    /// </summary>
    public static Handler Wrap(Handler handler) =>
        async envelope =>
        {
            string traceId = envelope.TraceId ?? string.Empty;
            string urn = EnvelopeCodec.Urn(envelope);

            ActivityContext parent = traceId.Length == 0
                ? default
                : new ActivityContext(TraceIdOf(traceId), SpanIdOf(traceId), ActivityTraceFlags.Recorded, isRemote: true);

            using Activity? activity = Source.StartActivity($"process {urn}", ActivityKind.Consumer, parent);
            ApplyConsumeTags(activity, envelope, traceId);

            try
            {
                await handler(envelope).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordError(activity, ex);
                throw;
            }
        };

    /// <summary>
    /// Runs a publish under a PRODUCER activity <c>publish &lt;urn&gt;</c>, carrying the active trace's
    /// id into the built envelope's <c>trace_id</c> so the downstream consumer recovers the same trace.
    /// <paramref name="send"/> performs the real transport write and its result is returned.
    /// </summary>
    public static async Task<TResult> PublishAsync<TResult>(
        string urn,
        IReadOnlyDictionary<string, object?>? data,
        Func<Envelope, Task<TResult>> send,
        string queue = "default")
    {
        ArgumentNullException.ThrowIfNull(send);

        using Activity? activity = Source.StartActivity($"publish {urn}", ActivityKind.Producer);
        activity?.SetTag("messaging.system", System);
        activity?.SetTag("messaging.operation", "publish");
        activity?.SetTag("messaging.destination.name", urn);

        try
        {
            string traceId = activity is not null ? UuidOf(activity.TraceId) : Guid.NewGuid().ToString();
            Envelope envelope = EnvelopeCodec.Make(urn, data, queue, traceId);
            TResult result = await send(envelope).ConfigureAwait(false);
            activity?.SetTag("messaging.message.id", envelope.Meta?.Id);
            return result;
        }
        catch (Exception ex)
        {
            RecordError(activity, ex);
            throw;
        }
    }

    private static void ApplyConsumeTags(Activity? activity, Envelope envelope, string traceId)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("messaging.system", System);
        activity.SetTag("messaging.operation", "process");
        activity.SetTag("messaging.destination.name", envelope.Meta?.Queue ?? string.Empty);
        activity.SetTag("messaging.message.id", envelope.Meta?.Id ?? string.Empty);
        activity.SetTag("messaging.message.conversation_id", traceId);
        activity.SetTag("messaging.babelqueue.attempts", envelope.Attempts);
    }

    private static void RecordError(Activity? activity, Exception ex)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName },
            { "exception.message", ex.Message },
            { "exception.stacktrace", ex.ToString() },
        }));
    }

    private static ActivitySpanId SpanIdOf(string traceId)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("babelqueue-span:" + traceId));
        return ActivitySpanId.CreateFromBytes(digest.AsSpan(0, 8));
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
