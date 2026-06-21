using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BabelQueue.Outbox;

/// <summary>
/// The <b>write side</b> of the transactional outbox (ADR-0029): turn a BabelQueue
/// <see cref="Envelope"/> into a stored outbox row, so the message is persisted <i>atomically
/// with the business data</i> and a separate <see cref="OutboxRelay"/> publishes it later.
/// </summary>
/// <remarks>
/// <para>
/// Usage — the caller owns the transaction boundary (this is the whole point):
/// </para>
/// <code>
/// await using var tx = await db.BeginTransactionAsync(ct);
/// await db.InsertOrderAsync(order, tx, ct);                 // the business write
/// var envelope = EnvelopeCodec.Make("urn:order:placed", data, "orders");
/// await outbox.WriteAsync(envelope, ct);                    // same connection, same tx
/// await tx.CommitAsync(ct);                                 // both, or neither
/// </code>
/// <para>
/// Because both writes share one transaction, a crash can never leave the business row
/// committed without its message (the classic dual-write bug) — they commit or roll back
/// together. The handoff to the broker becomes a <i>local</i> problem the relay solves.
/// </para>
/// <para>
/// This helper is intentionally tiny and dependency-free: it only encodes via the frozen
/// <see cref="EnvelopeCodec"/> (GR-1 — the envelope bytes are stored unchanged; the outbox never
/// adds an envelope field) and delegates persistence to the injected <see cref="IOutboxStore"/>,
/// which the caller binds to their own DB over ADO.NET (GR-7). It does <b>not</b> begin/commit
/// anything.
/// </para>
/// </remarks>
public sealed class Outbox
{
    /// <summary>UTF-8 without a BOM, so the stored bytes are the codec's exact JSON.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IOutboxStore _store;

    /// <summary>Creates a writer over the caller-bound <paramref name="store"/>.</summary>
    /// <param name="store">The persistence seam the caller binds to their own DB.</param>
    public Outbox(IOutboxStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// Encode <paramref name="envelope"/> (frozen codec, bytes unchanged) and persist it via the
    /// store, inside the transaction the caller has already opened. The logical queue is captured
    /// from <c>meta.queue</c> (falling back to <c>"default"</c>) so the relay can publish to the
    /// right queue without decoding the body. Returns the new outbox row id (for the caller's own
    /// correlation, if wanted). Does <b>not</b> begin or commit any transaction.
    /// </summary>
    /// <param name="envelope">A canonical envelope from <see cref="EnvelopeCodec.Make"/> / <see cref="EnvelopeCodec.FromMessage"/>.</param>
    /// <param name="cancellationToken">Cancels the persist operation.</param>
    /// <returns>The outbox row id.</returns>
    public Task<string> WriteAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        string queue = QueueOf(envelope);
        byte[] encoded = Utf8.GetBytes(EnvelopeCodec.Encode(envelope));

        return _store.SaveAsync(encoded, queue, cancellationToken);
    }

    /// <summary>
    /// The logical queue the message targets: its <c>meta.queue</c>, falling back to
    /// <c>"default"</c>. Captured at write time so the relay can publish to the right queue
    /// without decoding the body.
    /// </summary>
    private static string QueueOf(Envelope envelope)
    {
        string? queue = envelope.Meta?.Queue;
        return string.IsNullOrEmpty(queue) ? "default" : queue;
    }
}
