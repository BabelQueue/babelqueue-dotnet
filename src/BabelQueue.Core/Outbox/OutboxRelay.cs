using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BabelQueue.Outbox;

/// <summary>
/// Publishes one pending outbox row's <b>verbatim</b> stored body to its queue. This is the
/// publish-only seam the <see cref="OutboxRelay"/> forwards through — the .NET counterpart of the
/// PHP <c>Transport::publish(body, queue)</c> contract. The caller binds it to their broker (or to
/// an adapter's <c>PublishAsync(queue, body)</c>); it must publish <paramref name="body"/> exactly
/// as given, never decoding or rebuilding the envelope (GR-1/GR-5, <c>trace_id</c> preserved GR-4).
/// </summary>
/// <param name="body">The frozen, encoded envelope JSON, byte-for-byte as stored.</param>
/// <param name="queue">The logical queue to publish to.</param>
/// <param name="cancellationToken">Cancels the publish.</param>
public delegate Task OutboxPublisher(string body, string queue, CancellationToken cancellationToken);

/// <summary>
/// An async delay used between a failed publish and continuing the batch. Injectable so tests are
/// instant (a no-op records the requested delay without sleeping).
/// </summary>
/// <param name="milliseconds">The requested backoff, in milliseconds.</param>
/// <param name="cancellationToken">Cancels the delay.</param>
public delegate Task OutboxDelay(int milliseconds, CancellationToken cancellationToken);

/// <summary>
/// The <b>read/publish side</b> of the transactional outbox (ADR-0029): drain pending rows the
/// <see cref="Outbox"/> writer committed and forward each onto the broker through an injected
/// <see cref="OutboxPublisher"/>, marking every row published or failed.
/// </summary>
/// <remarks>
/// <para>
/// Run it on a short interval (a worker loop, a scheduled task) <i>after</i> the business
/// transaction commits. Because the message was committed atomically with the business data, the
/// relay is the only thing standing between "row exists" and "broker has it" — and it only ever
/// reads already-durable rows, so it never invents work.
/// </para>
/// <para><b>Semantics — at-least-once handoff:</b></para>
/// <list type="bullet">
/// <item>A row is marked <b>published only after</b> the <see cref="OutboxPublisher"/> returns; if
/// the process dies between publish and <see cref="IOutboxStore.MarkPublishedAsync"/>, the row
/// stays pending and is published <b>again</b> on the next pass. That is at-least-once: a
/// downstream consumer must dedupe on the canonical <c>meta.id</c> (<see cref="Idempotency.Wrap"/>
/// is exactly that guard, the consumer-side mirror of this producer-side helper — ADR-0022).</item>
/// <item>A publish that <b>throws</b> is caught, <see cref="IOutboxStore.MarkFailedAsync"/> records
/// the error and bumps the attempt count, and the row stays pending for a later retry. One poison
/// row never blocks the rest of the batch.</item>
/// <item><b><c>trace_id</c> is preserved end-to-end</b> (GR-4): the relay publishes the stored
/// bytes <i>verbatim</i> — it never decodes, rebuilds or re-encodes the envelope — so the body that
/// reaches the broker is byte-identical to what was stored (GR-1/GR-5).</item>
/// </list>
/// <para>
/// <b>Backoff:</b> between a failed publish and continuing the same pass the relay awaits a
/// bounded, linearly-growing delay (capped), to avoid hammering a broker that is briefly down. The
/// <see cref="OutboxDelay"/> is injectable so tests stay instant.
/// </para>
/// </remarks>
public sealed class OutboxRelay
{
    /// <summary>Hard safety ceiling on <see cref="DrainAsync"/> passes when the caller passes 0.</summary>
    private const int DefaultDrainCeiling = 10_000;

    private readonly OutboxPublisher _publisher;
    private readonly IOutboxStore _store;
    private readonly int _batchSize;
    private readonly int _backoffStepMs;
    private readonly int _backoffCapMs;
    private readonly OutboxDelay _delay;

    /// <summary>
    /// Creates a relay over the publish seam and outbox store.
    /// </summary>
    /// <param name="publisher">Where published rows go — the publish-only seam (verbatim body + queue).</param>
    /// <param name="store">The outbox to drain.</param>
    /// <param name="batchSize">How many rows to reserve and publish per <see cref="FlushAsync"/>.</param>
    /// <param name="backoffStepMs">Base backoff added per prior attempt, in milliseconds.</param>
    /// <param name="backoffCapMs">Upper bound on a single backoff delay, in milliseconds.</param>
    /// <param name="delay">Awaitable delay; defaults to <see cref="Task.Delay(int, CancellationToken)"/>. Inject a no-op in tests.</param>
    public OutboxRelay(
        OutboxPublisher publisher,
        IOutboxStore store,
        int batchSize = 100,
        int backoffStepMs = 50,
        int backoffCapMs = 5000,
        OutboxDelay? delay = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(store);

        _publisher = publisher;
        _store = store;
        _batchSize = batchSize;
        _backoffStepMs = backoffStepMs;
        _backoffCapMs = backoffCapMs;
        _delay = delay ?? (static (ms, ct) => ms > 0 ? Task.Delay(ms, ct) : Task.CompletedTask);
    }

    /// <summary>
    /// Publish one batch of pending rows. Each row the publisher accepts is marked published; each
    /// that throws is marked failed (with a backoff before continuing) and left pending. Returns a
    /// per-pass tally. Call it repeatedly (a loop / scheduler) to drain the outbox;
    /// <see cref="DrainAsync"/> loops until it is empty.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass between rows.</param>
    public async Task<OutboxRelayResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OutboxRecord> records =
            await _store.FetchUnpublishedAsync(_batchSize, cancellationToken).ConfigureAwait(false);

        var publishedIds = new List<string>(records.Count);
        int failed = 0;

        foreach (OutboxRecord record in records)
        {
            try
            {
                // Verbatim: the exact bytes that were stored are forwarded — never decoded/rebuilt.
                await _publisher(record.Body, record.Queue, cancellationToken).ConfigureAwait(false);
                publishedIds.Add(record.Id);
            }
            catch (Exception ex)
            {
                await _store.MarkFailedAsync(record.Id, Reason(ex), cancellationToken).ConfigureAwait(false);
                failed++;
                await _delay(BackoffFor(record.Attempts), cancellationToken).ConfigureAwait(false);
            }
        }

        if (publishedIds.Count > 0)
        {
            await _store.MarkPublishedAsync(publishedIds, cancellationToken).ConfigureAwait(false);
        }

        return new OutboxRelayResult(publishedIds.Count, failed);
    }

    /// <summary>
    /// Drain the outbox by repeatedly calling <see cref="FlushAsync"/> while each pass keeps making
    /// progress (publishes at least one row), then return the cumulative tally. The loop stops as
    /// soon as a pass publishes nothing — the outbox is empty, or only currently failing rows remain
    /// (those are left pending for a future <see cref="DrainAsync"/> call once the broker recovers).
    /// <paramref name="maxPasses"/> is a hard safety ceiling so a degenerate store can never spin
    /// forever (0 = a generous internal default).
    /// </summary>
    /// <param name="maxPasses">Hard ceiling on passes; 0 selects the internal default.</param>
    /// <param name="cancellationToken">Cancels the drain between passes.</param>
    public async Task<OutboxRelayResult> DrainAsync(int maxPasses = 0, CancellationToken cancellationToken = default)
    {
        int ceiling = maxPasses > 0 ? maxPasses : DefaultDrainCeiling;
        int published = 0;
        int failed = 0;

        for (int pass = 0; pass < ceiling; pass++)
        {
            OutboxRelayResult result = await FlushAsync(cancellationToken).ConfigureAwait(false);
            published += result.Published;
            failed += result.Failed;

            // No progress this pass → drained, or only failing rows remain. Stop.
            if (result.Published == 0)
            {
                break;
            }
        }

        return new OutboxRelayResult(published, failed);
    }

    /// <summary>
    /// The backoff (ms) for a row that has already failed <paramref name="priorAttempts"/> times: a
    /// linear step per attempt, capped. Kept simple and deterministic so the budget is obvious.
    /// </summary>
    private int BackoffFor(int priorAttempts)
    {
        int delay = _backoffStepMs * Math.Max(1, priorAttempts + 1);
        return Math.Min(delay, _backoffCapMs);
    }

    /// <summary>A short, safe failure reason from a thrown error (type + message, no stack).</summary>
    private static string Reason(Exception ex) => $"{ex.GetType().FullName}: {ex.Message}";
}
