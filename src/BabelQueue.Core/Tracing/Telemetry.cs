using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BabelQueue;

namespace BabelQueue.Tracing;

/// <summary>
/// Optional OpenTelemetry tracing for a babelqueue producer or consumer (ADR-0025, span shape per
/// ADR-0038) — the .NET mirror of the Go <c>otel</c>, Python <c>babelqueue.otel</c> and Node
/// <c>@babelqueue/core/otel</c> helpers. It emits a CONSUMER <see cref="Activity"/> <c>process
/// &lt;queue&gt;</c> per handled message and a PRODUCER activity <c>send &lt;queue&gt;</c> per publish,
/// correlating them across every hop and SDK through the envelope's <c>trace_id</c> — a UUID, which
/// maps 1:1 to a 16-byte <see cref="ActivityTraceId"/>. The standard metric set of ADR-0039 is
/// recorded on the <c>BabelQueue</c> <see cref="System.Diagnostics.Metrics.Meter"/> (see
/// <see cref="TelemetryMetrics"/>).
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
/// <b>Broker name (ADR-0038 section 2).</b> The wrapper sees a handler and an envelope, never the
/// transport, so the caller names the broker with the optional <c>system</c> parameter (for example
/// <c>"kafka"</c>, <c>"rabbitmq"</c>, <c>"aws_sqs"</c>; see <see cref="Systems"/>). It becomes
/// <c>messaging.system</c>. A value outside that set is written as given and never validated.
/// <b>An absent or empty <c>system</c> keeps the default <c>babelqueue</c>, which is deprecated</b>
/// (it matches no APM messaging view); what an absent value means after the deprecation window is
/// not decided yet. <c>consumerGroup</c> becomes <c>messaging.consumer.group.name</c> on the
/// consumer span (intended for Kafka; written whenever given).
/// </para>
/// <para>
/// <b>Dashboard-breaking change.</b> The span names are <c>send &lt;queue&gt;</c> /
/// <c>process &lt;queue&gt;</c> (they were <c>publish &lt;urn&gt;</c> / <c>process &lt;urn&gt;</c>), the
/// producer's <c>messaging.destination.name</c> is the queue (it was the URN) and
/// <c>messaging.system</c> is the <c>system</c> argument. The URN moved to <c>babelqueue.urn</c>.
/// The deprecated keys <c>messaging.operation</c> and <c>messaging.babelqueue.attempts</c> are
/// written next to their replacements for this MINOR only (dual-emit) and are removed in the next.
/// </para>
/// <para>
/// <c>trace_id</c> correlation keeps every hop that shares a <c>trace_id</c> in one trace. For
/// exact cross-hop <i>span</i> parent-child linkage — propagating a W3C <c>traceparent</c> as an
/// out-of-band transport header (ADR-0028, v0.2) — use the header-aware
/// <see cref="Wrap(Handler, IReadOnlyDictionary{string, string})"/> and
/// <see cref="PublishAsync{TResult}(string, IReadOnlyDictionary{string, object?}?, IDictionary{string, string}?, Func{Envelope, Task{TResult}}, string, string?)"/>
/// overloads with <see cref="Traceparent"/>; a message produced without a <c>traceparent</c>
/// header falls back to this <c>trace_id</c> mapping (no regression).
/// </para>
/// </remarks>
public static class Telemetry
{
    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource"/> these helpers emit on.
    /// Add it to an OpenTelemetry <c>TracerProvider</c> (<c>AddSource("BabelQueue")</c>) to export.
    /// </summary>
    public const string ActivitySourceName = "BabelQueue";

    /// <summary>The OpenTelemetry semantic-conventions release this helper targets (ADR-0038 section 1).</summary>
    public const string SemconvVersion = "1.44.0";

    /// <summary>
    /// The schema URL of <see cref="SemconvVersion"/>. The <c>net8.0</c> <see cref="ActivitySource"/> and
    /// <c>Meter</c> have no schema-URL property (it arrives with <c>ActivitySourceOptions</c> in .NET 9),
    /// so the core cannot attach it; a host that builds its own source or meter can use this constant.
    /// </summary>
    public const string SchemaUrl = "https://opentelemetry.io/schemas/" + SemconvVersion;

    /// <summary>The <c>messaging.system</c> default when the caller passes none (deprecated, ADR-0038 section 5).</summary>
    public const string DefaultSystem = "babelqueue";

    private const string ZeroTraceId = "00000000000000000000000000000000";
    private const int MaxWarnedTraceIds = 1024;

    private static readonly ActivitySource Source = new(ActivitySourceName, TelemetryMetrics.LibraryVersion);

    // ADR-0038 section 7: warn once per distinct non-UUID trace_id; the set is bounded so a producer
    // that emits endless distinct values cannot grow it (or flood the log) without limit.
    private static readonly ConcurrentDictionary<string, byte> WarnedTraceIds = new(StringComparer.Ordinal);

    /// <summary>
    /// The <c>messaging.system</c> values ADR-0038 section 3 lists, as guidance for callers (the set is
    /// not an allow-list; any other value is written as given).
    /// </summary>
    public static class Systems
    {
        /// <summary>Redis (binding 1) — a custom value; <c>redis</c> is not a semconv well-known one.</summary>
        public const string Redis = "redis";

        /// <summary>RabbitMQ (binding 2).</summary>
        public const string RabbitMq = "rabbitmq";

        /// <summary>Amazon SQS (binding 3).</summary>
        public const string AwsSqs = "aws_sqs";

        /// <summary>Azure Service Bus (binding 4).</summary>
        public const string ServiceBus = "servicebus";

        /// <summary>Apache Pulsar (binding 5).</summary>
        public const string Pulsar = "pulsar";

        /// <summary>Apache Kafka (binding 6).</summary>
        public const string Kafka = "kafka";

        /// <summary>Apache ActiveMQ / Artemis (binding 7).</summary>
        public const string ActiveMq = "activemq";
    }

    /// <summary>
    /// Maps an envelope <c>trace_id</c> to a deterministic <see cref="ActivityTraceId"/>: a UUID
    /// maps to its 16 hex bytes; any other string is hashed (SHA-256, first 16 bytes). The inverse
    /// of <see cref="UuidOf"/> for the UUID case.
    /// </summary>
    public static ActivityTraceId TraceIdOf(string traceId)
    {
        if (TryDecodeUuid(traceId, out ActivityTraceId decoded))
        {
            return decoded;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(traceId ?? string.Empty));
        return ActivityTraceId.CreateFromBytes(digest.AsSpan(0, 16));
    }

    /// <summary>
    /// Decodes a UUID-shaped <c>trace_id</c> (32 hex digits once the hyphens are removed, case
    /// insensitive, not all zero) to its 16 bytes. No trim: padding makes the value non-UUID.
    /// </summary>
    private static bool TryDecodeUuid(string? traceId, out ActivityTraceId decoded)
    {
        string hex = (traceId ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (hex.Length == 32 && IsHex(hex) && hex != ZeroTraceId)
        {
            decoded = ActivityTraceId.CreateFromString(hex.AsSpan());
            return true;
        }

        decoded = default;
        return false;
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
    /// Returns <paramref name="handler"/> wrapped to emit a CONSUMER activity <c>process &lt;queue&gt;</c>
    /// (ADR-0038) in the trace derived from the envelope's <c>trace_id</c>, recording the handler's
    /// error/status. The handler still receives the full <see cref="Envelope"/>; a throw is recorded and
    /// re-thrown so the runtime's retry / dead-letter path still applies. Metrics (ADR-0039) are recorded
    /// on every delivery.
    /// </summary>
    public static Handler Wrap(Handler handler) => Wrap(handler, headers: null);

    /// <summary>
    /// The header-aware (ADR-0028, v0.2) counterpart of <see cref="Wrap(Handler)"/>: starts the
    /// CONSUMER activity <c>process &lt;queue&gt;</c> as a true <b>child</b> of the producer span
    /// when the delivered message carried a valid W3C <c>traceparent</c> on
    /// <paramref name="headers"/> (an out-of-band carrier an adapter surfaces from the transport's
    /// metadata channel, beside the frozen envelope). When no usable <c>traceparent</c> is present
    /// it falls back to the v0.1 <c>trace_id</c>-derived parent — so enabling propagation is a
    /// strict, backward-compatible upgrade.
    /// </summary>
    /// <param name="handler">The consume handler to decorate; it still receives the full envelope.</param>
    /// <param name="headers">
    /// The delivered message's out-of-band headers, or <c>null</c> when the adapter carries none
    /// (then behaviour is exactly <see cref="Wrap(Handler)"/>).
    /// </param>
    public static Handler Wrap(Handler handler, IReadOnlyDictionary<string, string>? headers) =>
        Wrap(handler, headers, system: null, consumerGroup: null);

    /// <summary>
    /// The full form of <see cref="Wrap(Handler, IReadOnlyDictionary{string, string})"/>, naming the
    /// broker and the consumer group (ADR-0038 section 2).
    /// </summary>
    /// <param name="handler">The consume handler to decorate; it still receives the full envelope.</param>
    /// <param name="headers">The delivered message's out-of-band headers, or <c>null</c>.</param>
    /// <param name="system">
    /// The <c>messaging.system</c> value (for example <c>"kafka"</c>; see <see cref="Systems"/>). Absent or
    /// empty keeps the deprecated default <see cref="DefaultSystem"/>. It also labels the metrics.
    /// </param>
    /// <param name="consumerGroup">
    /// The <c>messaging.consumer.group.name</c> span attribute, written whenever given (intended for
    /// Kafka). It is not a metric attribute (ADR-0039 section 2).
    /// </param>
    public static Handler Wrap(
        Handler handler,
        IReadOnlyDictionary<string, string>? headers = null,
        string? system = null,
        string? consumerGroup = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        string resolvedSystem = ResolveSystem(system);

        return async envelope =>
        {
            string traceId = envelope.TraceId ?? string.Empty;
            string? queue = envelope.Meta?.Queue;
            string? destination = HasDestination(queue) ? queue : null;
            string spanName = destination is null ? "process" : "process " + destination;

            // ADR-0028: a valid traceparent makes this span a true child of the producer span;
            // otherwise fall back to the v0.1 trace_id-derived parent (ADR-0025 Option 1).
            ActivityContext? remote = Traceparent.RemoteParentFromHeaders(headers);

            // Tags are set at start, so a sampler sees them (ADR-0038 section 4).
            using Activity? activity = Source.HasListeners()
                ? Source.StartActivity(
                    spanName,
                    ActivityKind.Consumer,
                    remote ?? ParentFromTraceId(traceId),
                    ConsumeTags(envelope, traceId, resolvedSystem, destination, consumerGroup))
                : null;

            TelemetryMetrics.RecordDelivery(resolvedSystem, destination, envelope.Attempts);
            long started = Stopwatch.GetTimestamp();
            string? errorType = null;
            try
            {
                await handler(envelope).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errorType = ErrorTypeOf(ex);
                RecordError(activity, ex, errorType);
                throw;
            }
            finally
            {
                TelemetryMetrics.RecordProcessDuration(
                    resolvedSystem, destination, Stopwatch.GetElapsedTime(started).TotalSeconds, errorType);
            }
        };
    }

    /// <summary>
    /// The remote parent <see cref="ActivityContext"/> derived from the envelope's
    /// <c>trace_id</c> (ADR-0025 Option 1, v0.1) — a blank <c>trace_id</c> yields the default
    /// (root) context. A <c>trace_id</c> that is not a UUID is hashed (and warned about once per
    /// distinct value, ADR-0038 section 7); it is never rejected or rewritten.
    /// </summary>
    private static ActivityContext ParentFromTraceId(string traceId)
    {
        if (traceId.Length == 0)
        {
            return default;
        }

        if (!TryDecodeUuid(traceId, out ActivityTraceId mapped))
        {
            WarnHashed(traceId);
            mapped = TraceIdOf(traceId);
        }

        return new ActivityContext(mapped, SpanIdOf(traceId), ActivityTraceFlags.Recorded, isRemote: true);
    }

    private static void WarnHashed(string traceId)
    {
        if (WarnedTraceIds.Count >= MaxWarnedTraceIds || !WarnedTraceIds.TryAdd(traceId, 0))
        {
            return;
        }

        // The value is escaped and truncated: it is untrusted input headed for a log line.
        string shown = traceId.Length > 64 ? traceId[..64] + "..." : traceId;
        var sb = new StringBuilder(shown.Length + 8);
        foreach (char c in shown)
        {
            sb.Append(char.IsControl(c) ? string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}") : c.ToString());
        }

        Trace.TraceWarning(
            "BabelQueue: trace_id \"" + sb + "\" is not a UUID; the OpenTelemetry mapping hashes it "
            + "(SHA-256, first 16 bytes) instead of decoding it. The value itself is preserved unchanged.");
    }

    /// <summary>
    /// Runs a publish under a PRODUCER activity <c>send &lt;queue&gt;</c> with the default
    /// <c>messaging.system</c> (<see cref="DefaultSystem"/>, deprecated). This is the pre-ADR-0038 signature, kept
    /// as its own overload so assemblies compiled against earlier Core versions keep binding to it
    /// (a trailing optional parameter would be a binary-breaking change); it forwards to
    /// <see cref="PublishAsync{TResult}(string, IReadOnlyDictionary{string, object?}?, Func{Envelope, Task{TResult}}, string, string?)"/>.
    /// </summary>
    /// <param name="urn">The message URN to publish.</param>
    /// <param name="data">The message payload, or <c>null</c> for an empty body.</param>
    /// <param name="send">Performs the real transport write; its result is returned.</param>
    /// <param name="queue">The destination queue stamped into <c>meta.queue</c>.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Task<TResult> PublishAsync<TResult>(
        string urn,
        IReadOnlyDictionary<string, object?>? data,
        Func<Envelope, Task<TResult>> send,
        string queue) =>
        PublishAsync(urn, data, headers: null, send, queue, system: null);

    /// <summary>
    /// Runs a publish under a PRODUCER activity <c>send &lt;queue&gt;</c> (ADR-0038), carrying the active
    /// trace's id into the built envelope's <c>trace_id</c> so the downstream consumer recovers the same
    /// trace. <paramref name="send"/> performs the real transport write and its result is returned.
    /// <c>messaging.client.sent.messages</c> is incremented once per call, success or failure.
    /// </summary>
    /// <param name="urn">The message URN to publish.</param>
    /// <param name="data">The message payload, or <c>null</c> for an empty body.</param>
    /// <param name="send">Performs the real transport write; its result is returned.</param>
    /// <param name="queue">The destination queue stamped into <c>meta.queue</c>.</param>
    /// <param name="system">
    /// The <c>messaging.system</c> value (see <see cref="Systems"/>); absent or empty keeps the deprecated
    /// default <see cref="DefaultSystem"/>.
    /// </param>
    public static Task<TResult> PublishAsync<TResult>(
        string urn,
        IReadOnlyDictionary<string, object?>? data,
        Func<Envelope, Task<TResult>> send,
        string queue = "default",
        string? system = null) =>
        PublishAsync(urn, data, headers: null, send, queue, system);

    /// <summary>
    /// The header-aware publish with the default <c>messaging.system</c> (<see cref="DefaultSystem"/>,
    /// deprecated). This is the pre-ADR-0038 signature, kept as its own overload for binary compatibility; it
    /// forwards to
    /// <see cref="PublishAsync{TResult}(string, IReadOnlyDictionary{string, object?}?, IDictionary{string, string}?, Func{Envelope, Task{TResult}}, string, string?)"/>.
    /// </summary>
    /// <param name="urn">The message URN to publish.</param>
    /// <param name="data">The message payload, or <c>null</c> for an empty body.</param>
    /// <param name="headers">The out-of-band carrier to receive the producer span's <c>traceparent</c>.</param>
    /// <param name="send">Performs the real transport write; its result is returned.</param>
    /// <param name="queue">The destination queue stamped into <c>meta.queue</c>.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Task<TResult> PublishAsync<TResult>(
        string urn,
        IReadOnlyDictionary<string, object?>? data,
        IDictionary<string, string>? headers,
        Func<Envelope, Task<TResult>> send,
        string queue) =>
        PublishAsync(urn, data, headers, send, queue, system: null);

    /// <summary>
    /// The header-aware (ADR-0028, v0.2) counterpart of
    /// <see cref="PublishAsync{TResult}(string, IReadOnlyDictionary{string, object?}?, Func{Envelope, Task{TResult}}, string, string?)"/>:
    /// in addition to stamping the active trace's id into the envelope's <c>trace_id</c> (v0.1),
    /// it injects the active PRODUCER span's W3C <c>traceparent</c> (and <c>tracestate</c>) into
    /// <paramref name="headers"/>. An adapter carries that same carrier beside the frozen envelope
    /// on the transport's metadata channel, so the consumer can start its span as a true
    /// <b>child</b> of this producer span. The header rides out of band, never in the envelope
    /// (GR-1); a transport that can't carry headers simply doesn't — no error, no regression.
    /// </summary>
    /// <param name="urn">The message URN to publish.</param>
    /// <param name="data">The message payload, or <c>null</c> for an empty body.</param>
    /// <param name="headers">
    /// The out-of-band carrier to receive the producer span's <c>traceparent</c>. When the
    /// PRODUCER activity has no listener (so no span is recorded), or this is <c>null</c>, the
    /// carrier is left untouched and only the v0.1 <c>trace_id</c> propagation applies.
    /// </param>
    /// <param name="send">Performs the real transport write; its result is returned.</param>
    /// <param name="queue">
    /// The destination queue stamped into <c>meta.queue</c> and reported as
    /// <c>messaging.destination.name</c> (not written when blank).
    /// </param>
    /// <param name="system">
    /// The <c>messaging.system</c> value (see <see cref="Systems"/>); absent or empty keeps the deprecated
    /// default <see cref="DefaultSystem"/>.
    /// </param>
    public static async Task<TResult> PublishAsync<TResult>(
        string urn,
        IReadOnlyDictionary<string, object?>? data,
        IDictionary<string, string>? headers,
        Func<Envelope, Task<TResult>> send,
        string queue = "default",
        string? system = null)
    {
        ArgumentNullException.ThrowIfNull(send);

        string resolvedSystem = ResolveSystem(system);

        // Same rule as EnvelopeCodec.Make: a null queue is stamped as "default", and the span reports meta.queue.
        queue ??= "default";

        // ADR-0038 section 4: the destination is the queue the envelope is built for (meta.queue),
        // which is known before the span starts, so the name needs no later update.
        string? destination = HasDestination(queue) ? queue : null;

        using Activity? activity = Source.StartActivity(
            destination is null ? "send" : "send " + destination,
            ActivityKind.Producer);
        if (activity is not null)
        {
            activity.SetTag("messaging.system", resolvedSystem);
            activity.SetTag("messaging.operation.type", "send");
            activity.SetTag("messaging.operation.name", "send");
            if (destination is not null)
            {
                activity.SetTag("messaging.destination.name", destination);
            }

            activity.SetTag("babelqueue.urn", urn);
            activity.SetTag("babelqueue.schema_version", EnvelopeCodec.SchemaVersion);
            activity.SetTag("messaging.operation", "publish"); // deprecated, dual-emit (ADR-0038 section 5)

            // ADR-0028: inject this producer span's traceparent onto the out-of-band carrier (when one
            // was supplied), beside the frozen envelope, for true cross-hop span linkage.
            if (headers is not null)
            {
                Traceparent.Inject(headers, activity);
            }
        }

        string? errorType = null;
        try
        {
            string traceId = activity is not null ? UuidOf(activity.TraceId) : Guid.NewGuid().ToString();
            Envelope envelope = EnvelopeCodec.Make(urn, data, queue, traceId);

            // Set before the transport write so a failing send still carries them (ADR-0038 section 4).
            activity?.SetTag("babelqueue.urn", envelope.Job);
            activity?.SetTag("messaging.message.id", envelope.Meta?.Id);
            activity?.SetTag("messaging.message.conversation_id", envelope.TraceId);

            return await send(envelope).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errorType = ErrorTypeOf(ex);
            RecordError(activity, ex, errorType);
            throw;
        }
        finally
        {
            TelemetryMetrics.RecordSent(resolvedSystem, destination, errorType);
        }
    }

    private static string ResolveSystem(string? system) => string.IsNullOrEmpty(system) ? DefaultSystem : system;

    /// <summary>A destination exists unless the queue is absent or blank under the closed ASCII whitespace set.</summary>
    internal static bool HasDestination(string? queue)
    {
        if (queue is null)
        {
            return false;
        }

        foreach (char c in queue)
        {
            if (c is not (' ' or '\t' or '\n' or '\v' or '\f' or '\r'))
            {
                return true;
            }
        }

        return false;
    }

    private static List<KeyValuePair<string, object?>> ConsumeTags(
        Envelope envelope, string traceId, string system, string? destination, string? consumerGroup)
    {
        string urn = EnvelopeCodec.Urn(envelope);
        string? messageId = envelope.Meta?.Id;
        var tags = new List<KeyValuePair<string, object?>>(13)
        {
            new("messaging.system", system),
            new("messaging.operation.type", "process"),
            new("messaging.operation.name", "process"),
            new("messaging.message.conversation_id", traceId),
            new("babelqueue.urn", urn),
            new("babelqueue.schema_version", envelope.Meta?.SchemaVersion ?? EnvelopeCodec.SchemaVersion),
            new("babelqueue.attempts", envelope.Attempts),
            // deprecated, dual-emit for one MINOR (ADR-0038 section 5)
            new("messaging.operation", "process"),
            new("messaging.babelqueue.attempts", envelope.Attempts),
        };
        if (destination is not null)
        {
            tags.Add(new("messaging.destination.name", destination));
        }

        if (!string.IsNullOrEmpty(messageId))
        {
            tags.Add(new("messaging.message.id", messageId));
        }

        if (!string.IsNullOrEmpty(consumerGroup))
        {
            tags.Add(new("messaging.consumer.group.name", consumerGroup));
        }

        return tags;
    }

    /// <summary>The <c>error.type</c> value: the exception's full type name, else <c>_OTHER</c> (ADR-0038 section 6).</summary>
    private static string ErrorTypeOf(Exception ex) => ex.GetType().FullName is { Length: > 0 } name ? name : "_OTHER";

    private static void RecordError(Activity? activity, Exception ex, string errorType)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("error.type", errorType);
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
