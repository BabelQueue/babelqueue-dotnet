using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BabelQueue.Tracing;

/// <summary>
/// W3C <c>traceparent</c> transport-header propagation for true cross-hop <i>span</i>
/// parent-child linkage (ADR-0028, implementing the ADR-0025 Option 2 follow-up) — the .NET
/// mirror of <c>babelqueue-go/otel</c>, <c>babelqueue-python</c>'s <c>babelqueue.otel</c> and
/// <c>@babelqueue/core/otel</c>.
/// </summary>
/// <remarks>
/// <para>
/// Cross-hop trace propagation works at two layered levels:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b><c>trace_id</c> ↔ <see cref="ActivityTraceId"/> (ADR-0025, v0.1):</b> the envelope's
/// <c>trace_id</c> maps 1:1 to an OTel trace id, so every hop that shares a <c>trace_id</c>
/// shares one trace — correlation and per-hop timing with zero wire/transport change. This is
/// <see cref="Telemetry"/>.
/// </description></item>
/// <item><description>
/// <b>W3C <c>traceparent</c> transport header (ADR-0028, v0.2):</b> the producer also injects
/// the active <see cref="Activity"/>'s span context as a <c>traceparent</c> (and
/// <c>tracestate</c>) header onto an out-of-band carrier that rides <b>beside</b> the frozen
/// envelope, never inside it (GR-1). The consumer reads it and starts its span as a true
/// <b>child</b> of the producer span — real cross-hop parent→child linkage and per-hop span
/// timing, not just a shared trace. When no <c>traceparent</c> header is present it falls back
/// to the v0.1 <c>trace_id</c> behaviour, so enabling propagation is a strict, backward-
/// compatible upgrade — never a regression.
/// </description></item>
/// </list>
/// <para>
/// Like the v0.1 helper this is built only on <see cref="System.Diagnostics.Activity"/> —
/// the BCL primitive the OpenTelemetry .NET SDK consumes — so the core takes <b>no</b>
/// OpenTelemetry package dependency (GR-7). The W3C parse/format is implemented here directly
/// against the frozen Trace Context format rather than pulling any propagator library. The wire
/// envelope is untouched and <c>trace_id</c> is preserved (GR-1, GR-4).
/// </para>
/// <para>
/// The seam name is the .NET counterpart of the Go <c>HeaderPublisher</c> /
/// <c>ReceivedMessage.Headers</c> and Node <c>HeaderCarrier</c>: an out-of-band header map
/// (<see cref="IReadOnlyDictionary{TKey,TValue}"/> to read, <see cref="IDictionary{TKey,TValue}"/>
/// to write) that an adapter carries on its transport's native per-message metadata channel.
/// Per-adapter transport wiring (the .NET transports live in the separate
/// <c>babelqueue-dotnet-sqs</c> / <c>-redis</c> / <c>-masstransit</c> repos) is a documented
/// follow-up; this core delivers the v0.2 mechanism.
/// </para>
/// </remarks>
public static class Traceparent
{
    /// <summary>
    /// The W3C out-of-band transport-header key that carries the active span context across a
    /// hop (ADR-0028). It rides on a header carrier beside the frozen envelope — the same seam
    /// as the replay-bypass marker (ADR-0027) — so a consumer can start its span as a true child
    /// of the producer span. The name is the W3C standard, so a babelqueue <c>traceparent</c>
    /// interoperates with any OTel SDK or W3C-compliant peer.
    /// </summary>
    public const string HeaderTraceparent = "traceparent";

    /// <summary>The W3C <c>tracestate</c> companion header (ADR-0028); carried verbatim when present.</summary>
    public const string HeaderTracestate = "tracestate";

    // W3C Trace Context `traceparent` is a frozen, fixed-width format:
    //   `<version>-<32-hex trace id>-<16-hex parent span id>-<2-hex flags>`
    // This pattern mirrors the standard propagator's acceptance rules exactly (version != ff,
    // non-zero trace/span ids), so acceptance matches a W3C-compliant peer without taking a
    // dependency on any propagator library. Trailing parts are rejected only for version "00".
    private const string TraceparentPattern =
        @"^\s?(?<version>(?!ff)[\da-f]{2})-(?<traceId>(?![0]{32})[\da-f]{32})-(?<spanId>(?![0]{16})[\da-f]{16})-(?<flags>[\da-f]{2})(?<rest>-.*)?\s?$";

    private static readonly Regex TraceparentRegex =
        new(TraceparentPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Writes the active <see cref="Activity"/>'s span context as a W3C <c>traceparent</c>
    /// (and <c>tracestate</c>, when present) into <paramref name="headers"/>, returning the same
    /// carrier. This is the <b>producer half</b> of cross-hop linkage (ADR-0028): an adapter
    /// hands the resulting carrier to its transport so it rides beside the frozen envelope and
    /// the consumer can reconstruct the remote parent.
    /// </summary>
    /// <remarks>
    /// When <paramref name="activity"/> (or <see cref="Activity.Current"/> when it is null) has
    /// no recordable id, the carrier is returned unchanged — a no-trace publish stays
    /// header-free (no regression).
    /// </remarks>
    /// <param name="headers">The carrier to write into; created when null.</param>
    /// <param name="activity">The source activity; defaults to <see cref="Activity.Current"/>.</param>
    public static IDictionary<string, string> Inject(
        IDictionary<string, string>? headers = null,
        Activity? activity = null)
    {
        headers ??= new Dictionary<string, string>(StringComparer.Ordinal);

        Activity? source = activity ?? Activity.Current;
        if (source is null)
        {
            return headers;
        }

        string? traceparent = Format(source.Context);
        if (traceparent is null)
        {
            return headers;
        }

        headers[HeaderTraceparent] = traceparent;
        if (!string.IsNullOrEmpty(source.TraceStateString))
        {
            headers[HeaderTracestate] = source.TraceStateString!;
        }

        return headers;
    }

    /// <summary>
    /// Builds the remote parent <see cref="ActivityContext"/> extracted from a delivered message's
    /// out-of-band <paramref name="headers"/>, or <c>null</c> when there is no valid
    /// <c>traceparent</c>. This is the <b>consumer half</b> of cross-hop linkage (ADR-0028): an
    /// <see cref="Activity"/> started with the returned context is a child of the producer's span
    /// (remote parent), preserving per-hop span timing and real parent→child links.
    /// </summary>
    /// <remarks>
    /// A <c>null</c> return signals the caller to fall back to the v0.1 <c>trace_id</c>-derived
    /// parent (<see cref="Telemetry"/>), so a malformed, empty or absent header never regresses a
    /// message produced by a pre-0028 (or header-blind) producer.
    /// </remarks>
    public static ActivityContext? RemoteParentFromHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null
            || !headers.TryGetValue(HeaderTraceparent, out string? traceparent)
            || string.IsNullOrEmpty(traceparent))
        {
            return null;
        }

        headers.TryGetValue(HeaderTracestate, out string? tracestate);
        return Parse(traceparent, tracestate);
    }

    /// <summary>
    /// Formats an <see cref="ActivityContext"/> as a W3C <c>traceparent</c> header string, or
    /// <c>null</c> when the context is not valid (no trace/span id). Identical wire form to the
    /// standard propagator.
    /// </summary>
    public static string? Format(ActivityContext context)
    {
        ActivityTraceId traceId = context.TraceId;
        ActivitySpanId spanId = context.SpanId;
        if (traceId == default || spanId == default)
        {
            return null;
        }

        // The W3C trailing flags byte is the low byte of the trace flags (bit 0 = sampled).
        int flags = (int)context.TraceFlags & 0xFF;
        return $"00-{traceId.ToHexString()}-{spanId.ToHexString()}-{flags:x2}";
    }

    /// <summary>
    /// Parses a W3C <c>traceparent</c> header into a remote <see cref="ActivityContext"/>, or
    /// <c>null</c> when the header is missing or malformed. Mirrors the standard propagator's
    /// acceptance rules, so a garbage value never hijacks the trace — the caller falls back to the
    /// v0.1 parent.
    /// </summary>
    public static ActivityContext? Parse(string? traceparent, string? tracestate = null)
    {
        if (string.IsNullOrEmpty(traceparent))
        {
            return null;
        }

        Match match = TraceparentRegex.Match(traceparent);
        if (!match.Success)
        {
            return null;
        }

        // Per the W3C spec, only reject trailing parts when the version is the known "00".
        if (match.Groups["version"].Value == "00" && match.Groups["rest"].Success)
        {
            return null;
        }

        ActivityTraceId traceId = ActivityTraceId.CreateFromString(match.Groups["traceId"].Value.AsSpan());
        ActivitySpanId spanId = ActivitySpanId.CreateFromString(match.Groups["spanId"].Value.AsSpan());

        ActivityTraceFlags flags = (Convert.ToInt32(match.Groups["flags"].Value, 16) & 0x01) != 0
            ? ActivityTraceFlags.Recorded
            : ActivityTraceFlags.None;

        return new ActivityContext(
            traceId,
            spanId,
            flags,
            string.IsNullOrEmpty(tracestate) ? null : tracestate,
            isRemote: true);
    }
}
