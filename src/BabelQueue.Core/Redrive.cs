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
            var bypassed = false;
            try
            {
                bypassed = await PublishRedrivenAsync(transport, target, encoded, options.Bypass).ConfigureAwait(false);
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
            items.Add(new Item(messageId, envelope.TraceId, envelope.Job, reason, dlq, target, true, bypassed));
        }

        return new Result(redriven, skipped, items);
    }

    /// <summary>
    /// Re-publishes a reset <paramref name="body"/> to <paramref name="queue"/>. When
    /// <paramref name="bypass"/> is set and the transport is an <see cref="IHeaderPublisher"/>, it
    /// stamps the <c>bq-replay-bypass</c> header and reports <c>true</c>; otherwise it publishes
    /// plainly and reports <c>false</c> (ADR-0027).
    /// </summary>
    private static async Task<bool> PublishRedrivenAsync(ITransport transport, string queue, string body, bool bypass)
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

        await transport.PublishAsync(queue, body).ConfigureAwait(false);
        return false;
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
