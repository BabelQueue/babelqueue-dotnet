using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BabelQueue.Outbox;

/// <summary>
/// The persistence seam for the <b>transactional outbox</b> (ADR-0029) — the durable
/// "outbox" table that an <see cref="Outbox"/> writer fills and an <see cref="OutboxRelay"/>
/// drains.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of the pattern is to remove the <i>dual write</i> a plain producer makes:
/// "commit the business row" <b>and</b> "publish to the broker" are two systems that can
/// disagree on a crash. Instead the message is written <b>into the same database, inside the
/// same transaction</b> as the business data — so it commits or rolls back atomically with it —
/// and a separate relay publishes it afterwards. No distributed transaction; exactly-once
/// <i>handoff</i> into the broker (then at-least-once on the wire, as always).
/// </para>
/// <para>
/// <b>The transaction boundary is the CALLER'S.</b> The core does not open, commit or roll back
/// anything: <see cref="SaveAsync"/> is invoked from <i>inside</i> a transaction the caller
/// already began (around its own <c>INSERT INTO orders …</c>), and the caller commits both
/// together. This keeps the core free of any DB driver (GR-7): the core defines this contract;
/// a concrete adapter binds it to a real connection over ADO.NET. The reference
/// <see cref="InMemoryOutboxStore"/> is for tests / single-process demos.
/// </para>
/// <para>
/// The stored value is the <b>frozen wire envelope, byte-for-byte unchanged</b> (GR-1): an
/// <see cref="EnvelopeCodec.Encode(Envelope)"/> string. The outbox adds its own bookkeeping
/// columns (id, status, attempts, timestamps) <i>around</i> the envelope; it never adds a field
/// <i>to</i> it. What the relay publishes is the same bytes that were stored.
/// </para>
/// </remarks>
public interface IOutboxStore
{
    /// <summary>
    /// Persist one encoded envelope into the outbox, <b>within the transaction the caller has
    /// already opened</b> around its business write. Returns the new row's outbox id (the
    /// store's own primary key — NOT <c>meta.id</c>), which the caller may keep for correlation.
    /// The body is stored verbatim; do not re-encode or mutate it.
    /// </summary>
    /// <param name="encoded">The <see cref="EnvelopeCodec.Encode(Envelope)"/> output (UTF-8 JSON), stored verbatim.</param>
    /// <param name="queue">The logical target queue, captured for the relay.</param>
    /// <param name="cancellationToken">Cancels the persist operation.</param>
    /// <returns>The outbox row id.</returns>
    Task<string> SaveAsync(byte[] encoded, string queue, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserve up to <paramref name="limit"/> rows that are pending publish, <b>oldest first</b>,
    /// so a relay can forward them. Implementations SHOULD lock/claim the rows they return (e.g.
    /// <c>SELECT … FOR UPDATE SKIP LOCKED</c>, or a <c>picked_at</c> claim) so two concurrent
    /// relays do not both publish the same row; at-least-once still tolerates a rare double send.
    /// The in-memory reference does not claim — that is the adapter's job.
    /// </summary>
    /// <param name="limit">Maximum rows to return (a positive batch size).</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>Pending rows, oldest first; empty when the outbox is drained.</returns>
    Task<IReadOnlyList<OutboxRecord>> FetchUnpublishedAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark the given outbox rows as successfully published (so they are never relayed again).
    /// Called by the relay only <b>after</b> the transport accepted the message.
    /// </summary>
    /// <param name="ids">Outbox row ids previously returned by <see cref="FetchUnpublishedAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the mark.</param>
    Task MarkPublishedAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Record a failed publish attempt for one row: increment its attempt counter and store the
    /// last error, leaving it pending so a later relay pass retries it (at-least-once). A store
    /// MAY move a row that exceeds a max-attempts threshold to a terminal/parked state, but that
    /// policy is the adapter's, not the core's.
    /// </summary>
    /// <param name="id">The outbox row id.</param>
    /// <param name="reason">A short, human-readable failure reason (never secrets).</param>
    /// <param name="cancellationToken">Cancels the mark.</param>
    Task MarkFailedAsync(string id, string reason, CancellationToken cancellationToken = default);
}
