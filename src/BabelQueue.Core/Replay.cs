using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BabelQueue;

/// <summary>
/// Replay-Bypass — an out-of-band side-effect guard for DLQ replay (ADR-0027).
/// </summary>
/// <remarks>
/// <para>
/// A deliberate replay off the dead-letter queue (<see cref="Redrive"/>) re-runs the handler, and
/// the handler's external side-effects re-fire: a second charge, a duplicate email.
/// <see cref="Idempotency.Wrap"/> (ADR-0022) stops an <i>accidental</i> duplicate; it does not stop
/// the <i>intended</i> reprocess from re-firing effects that already happened. This closes that gap.
/// </para>
/// <para>
/// The marker that says "this is a replay, skip the external effects" rides <b>out of band</b> as
/// the <see cref="HeaderReplayBypass"/> transport header — never in the frozen envelope (GR-1,
/// <c>schema_version</c> stays 1). <see cref="Redrive.RedriveAsync"/> (with
/// <see cref="Redrive.Options.Bypass"/>) stamps it when the transport can carry headers; a consume
/// adapter, having reserved the message with its headers, passes them to the guard so the handler
/// can skip effects that already fired:
/// </para>
/// <code>
/// // CONSUMER: the adapter surfaces the delivered message's out-of-band headers.
/// await handler(envelope);                                  // idempotent core — always runs
/// await Replay.BypassExternalEffectsAsync(headers, async () =&gt; // skipped when IsReplay(headers)
/// {
///     await SendConfirmationEmailAsync(envelope);
/// });
/// </code>
/// <para>
/// The .NET core is codec-only (no runtime / no transport), mirroring the Go reference
/// <c>babelqueue-go/replay.go</c> and the Java <c>com.babelqueue.Replay</c>. Go threads the flag
/// through a <c>context.Context</c> and Java through a <c>ThreadLocal</c>; .NET has no ambient
/// consume scope here, so the delivered header map is passed explicitly — the same seam as the
/// <see cref="Tracing.Traceparent"/> consumer half. A concrete broker transport carries the header
/// once it implements the optional <see cref="Redrive.IHeaderPublisher"/> capability (the
/// per-adapter rollout, like the OpenTelemetry <c>traceparent</c> follow-up).
/// </para>
/// </remarks>
public static class Replay
{
    /// <summary>
    /// The out-of-band transport header <see cref="Redrive.RedriveAsync"/> (with
    /// <see cref="Redrive.Options.Bypass"/>) stamps on a replayed message, and that a consume
    /// adapter surfaces to the handler via <see cref="IsReplay"/> /
    /// <see cref="BypassExternalEffectsAsync"/>. It rides on a header carrier beside the frozen
    /// envelope, never inside it (GR-1) — the same seam as the <c>traceparent</c> header (ADR-0028).
    /// </summary>
    public const string HeaderReplayBypass = "bq-replay-bypass";

    /// <summary>
    /// Reports whether a delivered message was redriven with the replay-bypass marker — i.e. a
    /// deliberate replay whose external side-effects should be skipped. Reads the presence of
    /// <see cref="HeaderReplayBypass"/> on the message's out-of-band <paramref name="headers"/>
    /// (null/empty-safe: a header-less delivery is never a replay).
    /// </summary>
    /// <param name="headers">The delivered message's out-of-band transport headers (may be null).</param>
    public static bool IsReplay(IReadOnlyDictionary<string, string>? headers) =>
        headers is not null
        && headers.TryGetValue(HeaderReplayBypass, out string? value)
        && !string.IsNullOrEmpty(value);

    /// <summary>
    /// Runs <paramref name="effect"/> unless the delivered message is a
    /// <see cref="IsReplay">replay</see>, in which case it is skipped. Wrap the external,
    /// non-idempotent side of a handler — sending an email, charging a card, calling a third party —
    /// so a replay re-runs the idempotent core but does not re-fire effects that already happened.
    /// </summary>
    /// <param name="headers">The delivered message's out-of-band transport headers (may be null).</param>
    /// <param name="effect">The external side-effect to run only when this is not a replay.</param>
    public static async Task BypassExternalEffectsAsync(
        IReadOnlyDictionary<string, string>? headers,
        Func<Task> effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (IsReplay(headers))
        {
            return;
        }

        await effect().ConfigureAwait(false);
    }
}
