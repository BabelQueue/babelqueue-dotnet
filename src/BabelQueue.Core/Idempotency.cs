namespace BabelQueue;

/// <summary>
/// A consume handler: processes one decoded <see cref="Envelope"/>. May be async; a
/// thrown/faulted handler leaves the message unacknowledged so the runtime redelivers it
/// (the core is codec-only, so an adapter drives the actual consume loop).
/// </summary>
public delegate Task Handler(Envelope envelope);

/// <summary>
/// A pluggable record of message ids already processed, keyed on the envelope's
/// <c>meta.id</c>. The reference <see cref="InMemoryStore"/> is for tests / single-process
/// consumers; production backends (Redis, a database table) implement the same three
/// methods. "Seen-set" post-success dedupe — not exactly-once, not in-flight locking; a
/// transactional / outbox mode is a documented future direction (ADR-0022).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>Whether this message id has already been processed (remembered).</summary>
    bool Seen(string messageId);

    /// <summary>Records this message id as processed.</summary>
    void Remember(string messageId);

    /// <summary>Drops an id from the store (manual eviction; a backend may also expire ids).</summary>
    void Forget(string messageId);
}

/// <summary>
/// Process-local, thread-safe <see cref="IIdempotencyStore"/> backed by a set. For tests
/// and single-process consumers; not shared across workers and not persistent — use a
/// Redis- or database-backed store for production fleets.
/// </summary>
public sealed class InMemoryStore : IIdempotencyStore
{
    private readonly HashSet<string> _ids = new();
    private readonly object _gate = new();

    /// <inheritdoc/>
    public bool Seen(string messageId)
    {
        lock (_gate)
        {
            return _ids.Contains(messageId);
        }
    }

    /// <inheritdoc/>
    public void Remember(string messageId)
    {
        lock (_gate)
        {
            _ids.Add(messageId);
        }
    }

    /// <inheritdoc/>
    public void Forget(string messageId)
    {
        lock (_gate)
        {
            _ids.Remove(messageId);
        }
    }
}

/// <summary>
/// Wraps a <see cref="Handler"/> so a message whose <c>meta.id</c> was already processed
/// successfully is skipped (ADR-0022) — the .NET mirror of the PHP, Go, Python, and Node
/// helpers. A previously-seen id returns early (so an adapter acks it); a thrown/faulted
/// handler leaves the id unmarked so a redelivery runs it again; a message with no usable
/// <c>meta.id</c> runs unchanged.
/// </summary>
public static class Idempotency
{
    /// <summary>Returns <paramref name="handler"/> guarded by dedupe on <c>meta.id</c>.</summary>
    public static Handler Wrap(IIdempotencyStore store, Handler handler) =>
        async envelope =>
        {
            string? id = envelope.Meta?.Id;

            // No usable id → cannot dedupe; run the handler unchanged.
            if (string.IsNullOrEmpty(id))
            {
                await handler(envelope).ConfigureAwait(false);
                return;
            }

            // Already processed on an earlier delivery: return so the adapter acks it.
            if (store.Seen(id))
            {
                return;
            }

            // First success wins; a throw here leaves the id unmarked → retry/DLQ apply.
            await handler(envelope).ConfigureAwait(false);
            store.Remember(id);
        };
}
