using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BabelQueue.Outbox;

/// <summary>
/// Process-local, thread-safe reference <see cref="IOutboxStore"/> backed by a dictionary — for
/// tests and single-process demos. It has <b>no real transaction</b>: <see cref="SaveAsync"/> just
/// appends, so it cannot deliver the atomic-with-the-business-write guarantee a production store
/// gives. Use a database-backed adapter (binding the contract to ADO.NET) in production.
/// </summary>
/// <remarks>
/// It still faithfully models the relay contract: rows are pending until
/// <see cref="MarkPublishedAsync"/>, <see cref="FetchUnpublishedAsync"/> returns them oldest-first,
/// and <see cref="MarkFailedAsync"/> bumps the attempt count and stores the last error while leaving
/// the row pending for retry. It does <b>not</b> claim/lock the rows it returns (that is an
/// adapter's concern, per ADR-0029). The stored bytes are kept and returned verbatim (GR-1).
/// </remarks>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    /// <summary>UTF-8 without a BOM — the inverse of the writer's encoding, so the round-trip is byte-identical.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private sealed class Row
    {
        public required byte[] Body { get; init; }
        public required string Queue { get; init; }
        public int Attempts { get; set; }
        public bool Published { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _sequence;

    /// <inheritdoc/>
    public Task<string> SaveAsync(byte[] encoded, string queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        lock (_gate)
        {
            // A non-numeric id keeps the key a genuine string and mirrors the PHP reference.
            string id = "ob-" + (++_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Copy the caller's array so a later external mutation cannot alter the stored bytes.
            var stored = new byte[encoded.Length];
            Array.Copy(encoded, stored, encoded.Length);

            _rows[id] = new Row
            {
                Body = stored,
                Queue = queue ?? "default",
            };

            return Task.FromResult(id);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<OutboxRecord>> FetchUnpublishedAsync(int limit, CancellationToken cancellationToken = default)
    {
        var records = new List<OutboxRecord>();

        lock (_gate)
        {
            foreach (KeyValuePair<string, Row> entry in _rows)
            {
                if (entry.Value.Published)
                {
                    continue;
                }

                // Decode the verbatim stored bytes back to the codec's exact JSON string.
                records.Add(new OutboxRecord(
                    entry.Key,
                    Utf8.GetString(entry.Value.Body),
                    entry.Value.Queue,
                    entry.Value.Attempts));

                if (records.Count >= limit)
                {
                    break;
                }
            }
        }

        return Task.FromResult<IReadOnlyList<OutboxRecord>>(records);
    }

    /// <inheritdoc/>
    public Task MarkPublishedAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        lock (_gate)
        {
            foreach (string id in ids)
            {
                if (_rows.TryGetValue(id, out Row? row))
                {
                    row.Published = true;
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task MarkFailedAsync(string id, string reason, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_rows.TryGetValue(id, out Row? row))
            {
                row.Attempts++;
                row.Error = reason ?? string.Empty;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Test/inspection helper: the number of rows still pending publish.</summary>
    public int PendingCount()
    {
        lock (_gate)
        {
            int pending = 0;
            foreach (Row row in _rows.Values)
            {
                if (!row.Published)
                {
                    pending++;
                }
            }

            return pending;
        }
    }

    /// <summary>Test/inspection helper: the recorded attempt count for one row (0 if unknown).</summary>
    public int AttemptsOf(string id)
    {
        lock (_gate)
        {
            return _rows.TryGetValue(id, out Row? row) ? row.Attempts : 0;
        }
    }

    /// <summary>Test/inspection helper: the last recorded error for one row ('' if none).</summary>
    public string LastErrorOf(string id)
    {
        lock (_gate)
        {
            return _rows.TryGetValue(id, out Row? row) ? row.Error : string.Empty;
        }
    }
}
