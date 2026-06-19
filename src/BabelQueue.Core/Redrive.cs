using System;
using System.Collections.Generic;
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
/// Safety in v1 is <c>DryRun</c> + sandbox routing (<c>ToQueue</c>) + <c>Select</c>. The
/// <b>Replay-Bypass</b> guard (a <c>bq-replay-bypass</c> transport header surfaced to handlers so a
/// replay can skip external side-effects) is a documented phase two — it touches the runtime and
/// every transport, like the OpenTelemetry <c>traceparent</c> follow-up.
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

    /// <summary>Options for a <see cref="RedriveAsync"/> run.</summary>
    /// <param name="ToQueue">Overrides the target queue (sandbox/redirect); when blank, each message goes back to its own <c>dead_letter.original_queue</c>.</param>
    /// <param name="Max">Caps how many messages are pulled from the DLQ (0 = all available).</param>
    /// <param name="DryRun">Inspect and report the plan, restoring every message unchanged.</param>
    /// <param name="Select">Picks which messages to redrive (unselected are restored unchanged).</param>
    public sealed record Options(
        string? ToQueue = null,
        int Max = 0,
        bool DryRun = false,
        Func<Envelope, bool>? Select = null);

    /// <summary>What happened to one message during a <see cref="RedriveAsync"/> run.</summary>
    public sealed record Item(
        string? MessageId,
        string? TraceId,
        string? Urn,
        string? Reason,
        string From,
        string? To,
        bool Redriven);

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
    /// restored rather than dropped. On a publish failure the message is restored to the DLQ
    /// before the error propagates.
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

        foreach (var message in batch)
        {
            // Decode never throws — a malformed/non-object body yields an empty envelope
            // (a null Job), which is not redrivable; restore it rather than drop it.
            var envelope = EnvelopeCodec.Decode(message.Body);
            if (string.IsNullOrWhiteSpace(envelope.Job))
            {
                await transport.PublishAsync(dlq, message.Body).ConfigureAwait(false);
                await transport.AckAsync(message).ConfigureAwait(false);
                skipped++;
                items.Add(new Item(null, null, null, null, dlq, null, false));
                continue;
            }

            var reason = envelope.DeadLetter?.Reason;
            var messageId = envelope.Meta?.Id;

            if (options.Select is not null && !options.Select(envelope))
            {
                await transport.PublishAsync(dlq, message.Body).ConfigureAwait(false);
                await transport.AckAsync(message).ConfigureAwait(false);
                skipped++;
                items.Add(new Item(messageId, envelope.TraceId, envelope.Job, reason, dlq, null, false));
                continue;
            }

            var target = !string.IsNullOrWhiteSpace(options.ToQueue) ? options.ToQueue : SourceQueueOf(envelope);
            if (string.IsNullOrWhiteSpace(target))
            {
                // no source/sandbox queue to send it to — leave it on the DLQ
                await transport.PublishAsync(dlq, message.Body).ConfigureAwait(false);
                await transport.AckAsync(message).ConfigureAwait(false);
                skipped++;
                items.Add(new Item(messageId, envelope.TraceId, envelope.Job, reason, dlq, null, false));
                continue;
            }

            if (options.DryRun)
            {
                await transport.PublishAsync(dlq, message.Body).ConfigureAwait(false);
                await transport.AckAsync(message).ConfigureAwait(false);
                skipped++;
                items.Add(new Item(messageId, envelope.TraceId, envelope.Job, reason, dlq, target, false));
                continue;
            }

            var encoded = EnvelopeCodec.Encode(Reset(envelope));
            var published = false;
            try
            {
                await transport.PublishAsync(target, encoded).ConfigureAwait(false);
                published = true;
            }
            finally
            {
                if (!published)
                {
                    // restore the original to the DLQ before the error propagates
                    await transport.PublishAsync(dlq, message.Body).ConfigureAwait(false);
                    await transport.AckAsync(message).ConfigureAwait(false);
                }
            }

            await transport.AckAsync(message).ConfigureAwait(false);
            redriven++;
            items.Add(new Item(messageId, envelope.TraceId, envelope.Job, reason, dlq, target, true));
        }

        return new Result(redriven, skipped, items);
    }

    private static string? SourceQueueOf(Envelope envelope)
    {
        if (envelope.DeadLetter is { OriginalQueue: var originalQueue }
            && !string.IsNullOrWhiteSpace(originalQueue))
        {
            return originalQueue;
        }

        return envelope.Meta?.Queue;
    }
}
