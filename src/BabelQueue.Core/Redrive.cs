using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BabelQueue;

/// <summary>
/// DLQ redrive tooling — safe replay off the dead-letter queue (ADR-0026).
/// </summary>
/// <remarks>
/// <para>
/// The operator-side counterpart to the runtime's dead-letter routing: it reads dead-lettered
/// messages off a DLQ and re-publishes each to its source queue (its
/// <c>dead_letter.original_queue</c>) or a chosen queue, <see cref="Reset"/> for reprocessing —
/// the <c>dead_letter</c> block removed and <c>attempts</c> reset to 0, while <c>job</c>,
/// <c>trace_id</c>, <c>data</c> and <c>meta</c> are preserved verbatim.
/// </para>
/// <para>
/// The codec-only .NET core has no transport, so <see cref="RedriveAsync"/> works over a minimal
/// <see cref="ITransport"/> seam the caller implements over their broker. The wire envelope stays
/// frozen (GR-1) and no dependency is added.
/// </para>
/// <para>
/// Safety in v1 is <c>DryRun</c> + sandbox routing (<c>ToQueue</c>) + <c>Select</c>. On top of
/// those, the <b>Replay-Bypass</b> guard (ADR-0027): with <see cref="Options.Bypass"/>, a redriven
/// message is stamped with the <c>bq-replay-bypass</c> transport header — surfaced to the handler
/// via <see cref="Replay.IsReplay"/> / <see cref="Replay.BypassExternalEffectsAsync"/> so a replay
/// can skip external side-effects that already fired. The marker rides the out-of-band header
/// carrier, never the frozen envelope (GR-1), and takes effect only when the transport implements
/// the optional <see cref="IHeaderPublisher"/>; otherwise it is a no-op and the per-item
/// <see cref="Item.Bypassed"/> flag stays <c>false</c>, exactly like the Go reference and the
/// per-transport rollout of the OpenTelemetry <c>traceparent</c> follow-up.
/// </para>
/// <para>
/// A transport that implements the optional <see cref="IKeyedRepublisher"/> (Kafka: the record
/// key, contract §6.5) is handed the reserved DLQ message for every re-publish — the redrive
/// write and the restore write back to the DLQ — so broker-native per-message metadata is carried
/// over; any other transport gets a plain publish.
/// </para>
/// <para>
/// When a restore write back to the DLQ fails, the message stays unacknowledged. A transport that
/// implements the optional <see cref="IRewinder"/> (Kafka: a commit is cumulative, so a later
/// commit would otherwise move the group past the unrestored record) is also asked to hand the
/// message back to the broker; if that fails too, both errors surface together. The same happens
/// whenever a run ends early for any other reason (a failed redrive write whose restore succeeded,
/// a failed acknowledgement/commit, a throwing <c>Select</c> callback): the rest of the drained
/// batch was popped but never processed, so it is handed back once, at the end of the run.
/// </para>
/// </remarks>
public static class Redrive
{
    /// <summary>A reserved DLQ message: its raw body plus a transport-specific ack handle.</summary>
    public sealed record Reserved(string Body, object? Handle);

    /// <summary>The minimal transport surface <see cref="RedriveAsync"/> needs.</summary>
    public interface ITransport
    {
        /// <summary>Reserve the next message from <paramref name="queue"/>, or null when empty.</summary>
        Task<Reserved?> PopAsync(string queue);

        /// <summary>Publish an already-encoded <paramref name="body"/> to <paramref name="queue"/>.</summary>
        Task PublishAsync(string queue, string body);

        /// <summary>Acknowledge (remove) a previously reserved message.</summary>
        Task AckAsync(Reserved message);
    }

    /// <summary>
    /// An optional <see cref="ITransport"/> capability: publish a body together with out-of-band
    /// transport <c>headers</c> (e.g. the <c>bq-replay-bypass</c> marker), for brokers that carry
    /// per-message metadata. The .NET counterpart of Go's optional <c>HeaderPublisher</c> and Node's
    /// <c>RedriveIO.publishWithHeaders</c>. A transport that does not implement it simply does not
    /// propagate headers — <see cref="RedriveAsync"/> falls back to plain
    /// <see cref="ITransport.PublishAsync"/> and <see cref="Options.Bypass"/> is a no-op (ADR-0027).
    /// </summary>
    public interface IHeaderPublisher
    {
        /// <summary>Publish <paramref name="body"/> to <paramref name="queue"/> with out-of-band <paramref name="headers"/>.</summary>
        Task PublishWithHeadersAsync(string queue, string body, IReadOnlyDictionary<string, string> headers);
    }

    /// <summary>
    /// An optional <see cref="ITransport"/> capability: re-publish a body on behalf of a message the
    /// transport itself reserved, carrying whatever native per-message metadata the broker attaches
    /// that plain <see cref="ITransport.PublishAsync"/> cannot express — for Kafka, the record key
    /// (contract §6.5). The .NET counterpart of Go's optional <c>KeyedRepublisher</c>.
    /// <see cref="RedriveAsync"/> uses it for both the write to the work topic and the restore write
    /// back to the DLQ; a transport that does not implement it gets a plain
    /// <see cref="ITransport.PublishAsync"/>. With <see cref="Options.Bypass"/> set, an
    /// <see cref="IHeaderPublisher"/> still takes precedence for the redrive write.
    /// </summary>
    public interface IKeyedRepublisher
    {
        /// <summary>
        /// Publish <paramref name="body"/> to <paramref name="queue"/> on behalf of <paramref name="from"/>,
        /// the reserved DLQ message the re-publish stands in for (never null).
        /// </summary>
        Task PublishFromAsync(string queue, string body, Reserved from);
    }

    /// <summary>
    /// An optional <see cref="ITransport"/> capability: hand an unacknowledged reserved message back
    /// to the broker so it is redelivered, and make sure nothing acknowledged afterwards can move
    /// past it. The .NET counterpart of Go's optional <c>Rewinder</c>. <see cref="RedriveAsync"/>
    /// calls it when a restore write back to the DLQ fails: for an offset-committing broker (Kafka,
    /// where a commit is cumulative) it rewinds the consumer to the message's offset, for a
    /// lock-based broker it releases the lock. It must never delete or acknowledge the message, nor
    /// alter its body. A transport whose unacknowledged messages already come back on their own
    /// need not implement it. <see cref="RedriveAsync"/> calls it once when a run ends early
    /// (a failed restore, a failed redrive write whose restore succeeded, a failed commit, a
    /// throwing <c>Select</c> callback): the message passed may then already be restored and
    /// acknowledged, and the call stands for the rest of the drained batch. An implementation
    /// must rewind to what is not yet committed and never past it, so an acknowledged message is
    /// not redelivered.
    /// </summary>
    public interface IRewinder
    {
        /// <summary>Hand the unacknowledged reserved <paramref name="message"/> back to the broker.</summary>
        Task RewindAsync(Reserved message);
    }

    /// <summary>Options for a <see cref="RedriveAsync"/> run.</summary>
    /// <param name="ToQueue">Overrides the target queue (sandbox/redirect); when blank, each message goes back to its own <c>dead_letter.original_queue</c>.</param>
    /// <param name="Max">Caps how many messages are pulled from the DLQ (0 = all available).</param>
    /// <param name="DryRun">Inspect and report the plan, restoring every message unchanged.</param>
    /// <param name="Select">Picks which messages to redrive (unselected are restored unchanged).</param>
    /// <param name="Bypass">Stamps the <c>bq-replay-bypass</c> header on each redriven message (see <see cref="Replay"/>); a no-op unless the transport is an <see cref="IHeaderPublisher"/>.</param>
    public sealed record Options(
        string? ToQueue = null,
        int Max = 0,
        bool DryRun = false,
        Func<Envelope, bool>? Select = null,
        bool Bypass = false);

    /// <summary>What happened to one message during a <see cref="RedriveAsync"/> run.</summary>
    public sealed record Item(
        string? MessageId,
        string? TraceId,
        string? Urn,
        string? Reason,
        string From,
        string? To,
        bool Redriven,
        bool Bypassed = false);

    /// <summary>Summary of a <see cref="RedriveAsync"/> run.</summary>
    public sealed record Result(int Redriven, int Skipped, IReadOnlyList<Item> Items);

    /// <summary>
    /// Returns a copy of <paramref name="envelope"/> reset for reprocessing: the
    /// <c>dead_letter</c> block removed and <c>attempts</c> reset to 0; everything else preserved.
    /// </summary>
    public static Envelope Reset(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope with { Attempts = 0, DeadLetter = null };
    }

    /// <summary>
    /// Drains dead-lettered messages off <paramref name="dlq"/> and re-publishes each (reset) to
    /// its source queue or <c>options.ToQueue</c>. Messages are drained first and then processed,
    /// so restored messages (skipped, dry-run, or undecodable) are not re-encountered; a DLQ
    /// message is acknowledged only after a successful re-publish, and an undecodable body is
    /// restored rather than dropped. On a publish failure the message is restored to the DLQ before
    /// the error propagates. Whatever ends the run early, an <see cref="IRewinder"/> transport is
    /// rewound once for the rest of the batch.
    /// </summary>
    public static async Task<Result> RedriveAsync(ITransport transport, string dlq, Options options)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);

        var batch = new List<Reserved>();
        while (options.Max == 0 || batch.Count < options.Max)
        {
            var message = await transport.PopAsync(dlq).ConfigureAwait(false);
            if (message is null)
            {
                break;
            }

            batch.Add(message);
        }

        var redriven = 0;
        var skipped = 0;
        var items = new List<Item>(batch.Count);
        var current = batch.Count > 0 ? batch[0] : null;

        try
        {
            foreach (var message in batch)
            {
                current = message;

                // Decode never throws — a malformed/non-object body yields an empty envelope
                // (a null Job), which is not redrivable; restore it rather than drop it.
                var envelope = EnvelopeCodec.Decode(message.Body);
                if (EnvelopeCodec.IsBlank(envelope.Job))
                {
                    await RestoreAsync(transport, dlq, message).ConfigureAwait(false);
                    skipped++;
                    items.Add(new Item(null, null, null, null, dlq, null, false));
                    continue;
                }

                var reason = envelope.DeadLetter?.Reason;
                var messageId = envelope.Meta?.Id;

                if (options.Select is not null && !options.Select(envelope))
                {
                    await RestoreAsync(transport, dlq, message).ConfigureAwait(false);
                    skipped++;
                    items.Add(new Item(messageId, envelope.TraceId, envelope.Job, reason, dlq, null, false));
                    continue;
                }

                var target = !EnvelopeCodec.IsBlank(options.ToQueue) ? options.ToQueue : SourceQueueOf(envelope);
                if (EnvelopeCodec.IsBlank(target))
                {
                    // no source/sandbox queue to send it to — leave it on the DLQ
                    await RestoreAsync(transport, dlq, message).ConfigureAwait(false);
                    skipped++;
                    items.Add(new Item(messageId, envelope.TraceId, envelope.Job, reason, dlq, null, false));
                    continue;
                }

                if (options.DryRun)
                {
                    await RestoreAsync(transport, dlq, message).ConfigureAwait(false);
                    skipped++;
                    items.Add(new Item(messageId, envelope.TraceId, envelope.Job, reason, dlq, target, false));
                    continue;
                }

                var encoded = EnvelopeCodec.Encode(Reset(envelope));
                bool bypassed;
                try
                {
                    bypassed = await PublishRedrivenAsync(transport, target, encoded, options.Bypass, message).ConfigureAwait(false);
                }
                catch (Exception publishError)
                {
                    // restore the original to the DLQ before the error propagates; if the restore
                    // fails too, surface both (the message is not acknowledged; the run-level handler
                    // below hands the transport back to the broker)
                    try
                    {
                        await RestoreAsync(transport, dlq, message).ConfigureAwait(false);
                    }
                    catch (Exception restoreError)
                    {
                        throw new AggregateException(publishError, restoreError);
                    }

                    throw;
                }

                await transport.AckAsync(message).ConfigureAwait(false);
                redriven++;
                items.Add(new Item(messageId, envelope.TraceId, envelope.Job, reason, dlq, target, true, bypassed));
            }

        }
        catch (Exception error) when (transport is IRewinder && current is not null)
        {
            // Whatever ended the run (a failed restore or commit, a publish failure, a throwing
            // Select callback), the rest of the drained batch was popped but never processed or
            // acknowledged: hand it back to the broker once, otherwise a later cumulative commit
            // by the same consumer could skip those records. Records already acknowledged are not
            // rewound (the transport tracks only the unsettled ones).
            try
            {
                await ((IRewinder)transport).RewindAsync(current).ConfigureAwait(false);
            }
            catch (Exception rewindError)
            {
                // keep the report flat: a publish+restore failure is already an aggregate, so the
                // rewind error joins it instead of nesting it one level down
                throw error is AggregateException combined
                    ? new AggregateException(combined.InnerExceptions.Append(rewindError))
                    : new AggregateException(error, rewindError);
            }

            throw;
        }

        return new Result(redriven, skipped, items);
    }

    /// <summary>
    /// Writes the reserved DLQ <paramref name="message"/> back to <paramref name="dlq"/> unchanged
    /// and acknowledges it only when that write succeeded. A failed write leaves the message
    /// unacknowledged and rethrows the write error: the original is never dropped. The caller
    /// (<see cref="RedriveAsync"/>) hands an <see cref="IRewinder"/> transport back to the broker.
    /// </summary>
    private static async Task RestoreAsync(ITransport transport, string dlq, Reserved message)
    {
        await RepublishAsync(transport, dlq, message.Body, message).ConfigureAwait(false);
        await transport.AckAsync(message).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-publishes a reset <paramref name="body"/> to <paramref name="queue"/>. When
    /// <paramref name="bypass"/> is set and the transport is an <see cref="IHeaderPublisher"/>, it
    /// stamps the <c>bq-replay-bypass</c> header and reports <c>true</c>; otherwise it publishes
    /// through <see cref="RepublishAsync"/> and reports <c>false</c> (ADR-0027).
    /// </summary>
    private static async Task<bool> PublishRedrivenAsync(
        ITransport transport, string queue, string body, bool bypass, Reserved from)
    {
        if (bypass && transport is IHeaderPublisher headerPublisher)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Replay.HeaderReplayBypass] = "1",
            };
            await headerPublisher.PublishWithHeadersAsync(queue, body, headers).ConfigureAwait(false);
            return true;
        }

        await RepublishAsync(transport, queue, body, from).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Re-publishes on behalf of <paramref name="from"/>: through <see cref="IKeyedRepublisher"/>
    /// when the transport offers it, otherwise a plain <see cref="ITransport.PublishAsync"/>.
    /// </summary>
    private static Task RepublishAsync(ITransport transport, string queue, string body, Reserved from) =>
        transport is IKeyedRepublisher keyed
            ? keyed.PublishFromAsync(queue, body, from)
            : transport.PublishAsync(queue, body);

    private static string? SourceQueueOf(Envelope envelope)
    {
        if (envelope.DeadLetter is { OriginalQueue: var originalQueue }
            && !EnvelopeCodec.IsBlank(originalQueue))
        {
            return originalQueue;
        }

        return envelope.Meta?.Queue;
    }
}
