using System;

namespace BabelQueue.Outbox;

/// <summary>
/// One pending row read back from an <see cref="IOutboxStore"/> for the
/// <see cref="OutboxRelay"/> to publish. It pairs the store's own bookkeeping
/// (<see cref="Id"/>, <see cref="Attempts"/>) with the verbatim, frozen wire envelope
/// (<see cref="Body"/>) and the queue it should go to.
/// </summary>
/// <remarks>
/// <see cref="Body"/> is the exact <see cref="EnvelopeCodec.Encode(Envelope)"/> output that
/// was handed to <see cref="IOutboxStore.SaveAsync"/> — the relay publishes these bytes
/// unchanged (GR-1/GR-5), so <c>trace_id</c> is preserved end-to-end (GR-4) without the
/// relay ever decoding or rebuilding the envelope.
/// </remarks>
/// <param name="Id">The outbox row id (the store's primary key, not <c>meta.id</c>).</param>
/// <param name="Body">The frozen, encoded envelope JSON, byte-for-byte as stored.</param>
/// <param name="Queue">The logical queue the relay should publish to.</param>
/// <param name="Attempts">How many times the relay has already tried to publish this row.</param>
public sealed record OutboxRecord(string Id, string Body, string Queue, int Attempts = 0);
