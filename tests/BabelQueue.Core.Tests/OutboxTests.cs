using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BabelQueue;
using BabelQueue.Outbox;
using Xunit;

// The writer type is BabelQueue.Outbox.Outbox; alias it so the bare name is unambiguous
// against the same-named namespace (CS0118). This is the documented C# pattern.
using OutboxWriter = BabelQueue.Outbox.Outbox;

namespace BabelQueue.Tests;

public sealed class OutboxTests
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// A fake <see cref="OutboxPublisher"/>: records every published (body, queue) pair, and can be
    /// told to throw for the first <c>failFirst</c> attempts (to model a broker that is briefly down)
    /// or whenever a body matches a predicate (to model one poison row).
    /// </summary>
    private sealed class FakePublisher
    {
        private readonly Func<string, bool>? _failWhen;
        private int _failFirst;

        public FakePublisher(int failFirst = 0, Func<string, bool>? failWhen = null)
        {
            _failFirst = failFirst;
            _failWhen = failWhen;
        }

        public List<(string Body, string Queue)> Published { get; } = new();

        public int Calls { get; private set; }

        public Task PublishAsync(string body, string queue, CancellationToken cancellationToken)
        {
            Calls++;

            if (_failFirst > 0)
            {
                _failFirst--;
                throw new InvalidOperationException("broker unavailable");
            }

            if (_failWhen is not null && _failWhen(body))
            {
                throw new InvalidOperationException("poison row");
            }

            Published.Add((body, queue));
            return Task.CompletedTask;
        }
    }

    /// <summary>An <see cref="OutboxDelay"/> that records the requested millisecond budgets without sleeping.</summary>
    private sealed class RecordingDelay
    {
        public List<int> Delays { get; } = new();

        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            Delays.Add(milliseconds);
            return Task.CompletedTask;
        }
    }

    private static Envelope SampleEnvelope(string urn = "urn:babel:orders:created", string queue = "orders", string? traceId = "trace-abc") =>
        EnvelopeCodec.Make(urn, new Dictionary<string, object?> { ["order_id"] = 1042L }, queue, traceId);

    // ── write side ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteStoresTheExactEncodedEnvelopeBytes()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);
        var envelope = SampleEnvelope();
        string encoded = EnvelopeCodec.Encode(envelope);

        string id = await outbox.WriteAsync(envelope);

        Assert.Equal("ob-1", id);
        Assert.Equal(1, store.PendingCount());

        var records = await store.FetchUnpublishedAsync(10);
        OutboxRecord record = Assert.Single(records);

        // GR-1/GR-5: the stored body is byte-for-byte the codec output.
        Assert.Equal(encoded, record.Body);
        Assert.Equal(Utf8.GetBytes(encoded), Utf8.GetBytes(record.Body));
        Assert.Equal("orders", record.Queue);
        Assert.Equal(0, record.Attempts);
    }

    [Fact]
    public async Task WriteCapturesQueueFromMetaAndFallsBackToDefault()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);

        await outbox.WriteAsync(SampleEnvelope(queue: "orders"));
        // Make() coerces a blank queue to "default", so an explicitly-blank queue lands as "default".
        await outbox.WriteAsync(SampleEnvelope(queue: ""));

        var records = await store.FetchUnpublishedAsync(10);
        Assert.Equal(2, records.Count);
        Assert.Equal("orders", records[0].Queue);
        Assert.Equal("default", records[1].Queue);
    }

    [Fact]
    public async Task WriteRejectsNullEnvelope()
    {
        var outbox = new OutboxWriter(new InMemoryOutboxStore());
        await Assert.ThrowsAsync<ArgumentNullException>(() => outbox.WriteAsync(null!));
    }

    [Fact]
    public void OutboxRejectsNullStore() => Assert.Throws<ArgumentNullException>(() => new OutboxWriter(null!));

    // ── relay: happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FlushPublishesVerbatimBodiesAndMarksPublished()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);
        var envelope = SampleEnvelope();
        string encoded = EnvelopeCodec.Encode(envelope);
        await outbox.WriteAsync(envelope);

        var publisher = new FakePublisher();
        var relay = new OutboxRelay(publisher.PublishAsync, store);

        OutboxRelayResult result = await relay.FlushAsync();

        Assert.Equal(1, result.Published);
        Assert.Equal(0, result.Failed);
        Assert.Equal(1, result.Attempted);

        // Published verbatim — byte-identical to what was stored (trace_id preserved, GR-4).
        var (body, queue) = Assert.Single(publisher.Published);
        Assert.Equal(encoded, body);
        Assert.Equal("orders", queue);
        Assert.Contains(envelope.TraceId!, body, StringComparison.Ordinal);

        // The row is gone from the pending set after a successful publish.
        Assert.Equal(0, store.PendingCount());
        Assert.Empty(await store.FetchUnpublishedAsync(10));
    }

    [Fact]
    public async Task RelayPreservesTraceIdEndToEnd()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);
        var envelope = SampleEnvelope(traceId: "00000000-0000-0000-0000-0000deadbeef");
        await outbox.WriteAsync(envelope);

        var publisher = new FakePublisher();
        var relay = new OutboxRelay(publisher.PublishAsync, store);
        await relay.FlushAsync();

        var (body, _) = Assert.Single(publisher.Published);
        // The relay never decodes/rebuilds — but decoding here proves trace_id survived the round-trip.
        Envelope republished = EnvelopeCodec.Decode(body);
        Assert.Equal("00000000-0000-0000-0000-0000deadbeef", republished.TraceId);
    }

    // ── relay: failure handling ─────────────────────────────────────────────────────

    [Fact]
    public async Task ThrowingPublishMarksFailedLeavesRowPendingAndBatchContinues()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);

        // Three rows; the middle one is poison.
        string idGood1 = await outbox.WriteAsync(SampleEnvelope(urn: "urn:babel:orders:a"));
        string idPoison = await outbox.WriteAsync(SampleEnvelope(urn: "urn:babel:orders:poison"));
        string idGood2 = await outbox.WriteAsync(SampleEnvelope(urn: "urn:babel:orders:b"));

        var publisher = new FakePublisher(failWhen: body => body.Contains("poison", StringComparison.Ordinal));
        var delay = new RecordingDelay();
        var relay = new OutboxRelay(publisher.PublishAsync, store, delay: delay.DelayAsync);

        OutboxRelayResult result = await relay.FlushAsync();

        // The poison row failed but did not block the two good rows.
        Assert.Equal(2, result.Published);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, publisher.Published.Count);

        // The two good rows are published; the poison row is still pending with attempt + error recorded.
        Assert.Equal(1, store.PendingCount());
        Assert.Equal(1, store.AttemptsOf(idPoison));
        Assert.Contains("poison row", store.LastErrorOf(idPoison), StringComparison.Ordinal);
        Assert.Equal(0, store.AttemptsOf(idGood1));
        Assert.Equal(0, store.AttemptsOf(idGood2));

        // A failure incurred one backoff delay.
        Assert.Single(delay.Delays);
    }

    [Fact]
    public async Task RetryAfterTransientFailureEventuallyPublishes()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);
        string id = await outbox.WriteAsync(SampleEnvelope());

        // Fails on the first attempt, succeeds on the second.
        var publisher = new FakePublisher(failFirst: 1);
        var delay = new RecordingDelay();
        var relay = new OutboxRelay(publisher.PublishAsync, store, delay: delay.DelayAsync);

        OutboxRelayResult first = await relay.FlushAsync();
        Assert.Equal(0, first.Published);
        Assert.Equal(1, first.Failed);
        Assert.Equal(1, store.AttemptsOf(id));
        Assert.Equal(1, store.PendingCount());

        OutboxRelayResult second = await relay.FlushAsync();
        Assert.Equal(1, second.Published);
        Assert.Equal(0, second.Failed);
        Assert.Equal(0, store.PendingCount());
    }

    // ── relay: drain ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DrainLoopsUntilTheOutboxIsEmpty()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);
        for (int i = 0; i < 5; i++)
        {
            await outbox.WriteAsync(SampleEnvelope(urn: $"urn:babel:orders:{i}"));
        }

        var publisher = new FakePublisher();
        // batchSize 2 forces multiple passes (3 passes: 2 + 2 + 1).
        var relay = new OutboxRelay(publisher.PublishAsync, store, batchSize: 2);

        OutboxRelayResult result = await relay.DrainAsync();

        Assert.Equal(5, result.Published);
        Assert.Equal(0, result.Failed);
        Assert.Equal(5, publisher.Published.Count);
        Assert.Equal(0, store.PendingCount());
    }

    [Fact]
    public async Task DrainStopsWhenOnlyFailingRowsRemain()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);
        await outbox.WriteAsync(SampleEnvelope(urn: "urn:babel:orders:ok"));
        await outbox.WriteAsync(SampleEnvelope(urn: "urn:babel:orders:poison"));

        var publisher = new FakePublisher(failWhen: body => body.Contains("poison", StringComparison.Ordinal));
        var delay = new RecordingDelay();
        var relay = new OutboxRelay(publisher.PublishAsync, store, batchSize: 2, delay: delay.DelayAsync);

        OutboxRelayResult result = await relay.DrainAsync();

        // First pass publishes the good row (progress) and fails the poison; the second pass makes
        // no progress (only the poison remains and it fails again) → drain stops.
        Assert.Equal(1, result.Published);
        Assert.Equal(2, result.Failed);
        Assert.Equal(1, store.PendingCount());
        Assert.Equal(2, store.AttemptsOf("ob-2"));
    }

    [Fact]
    public async Task DrainRespectsMaxPassesCeiling()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);
        for (int i = 0; i < 4; i++)
        {
            await outbox.WriteAsync(SampleEnvelope(urn: $"urn:babel:orders:{i}"));
        }

        var publisher = new FakePublisher();
        var relay = new OutboxRelay(publisher.PublishAsync, store, batchSize: 1);

        // One pass publishes one row; the ceiling of 2 passes stops after two rows.
        OutboxRelayResult result = await relay.DrainAsync(maxPasses: 2);

        Assert.Equal(2, result.Published);
        Assert.Equal(2, store.PendingCount());
    }

    // ── relay: backoff ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task BackoffGrowsLinearlyPerAttemptAcrossPasses()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);
        await outbox.WriteAsync(SampleEnvelope());

        // Always fails, so each pass bumps the row's attempt count and the next backoff grows.
        var publisher = new FakePublisher(failWhen: _ => true);
        var delay = new RecordingDelay();
        var relay = new OutboxRelay(publisher.PublishAsync, store, backoffStepMs: 50, backoffCapMs: 5000, delay: delay.DelayAsync);

        await relay.FlushAsync(); // attempts: 0 → backoff 50 * max(1, 0+1) = 50
        await relay.FlushAsync(); // attempts: 1 → backoff 50 * (1+1)       = 100
        await relay.FlushAsync(); // attempts: 2 → backoff 50 * (2+1)       = 150

        Assert.Equal(new[] { 50, 100, 150 }, delay.Delays);
    }

    [Fact]
    public async Task BackoffIsCappedAtTheConfiguredCeiling()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);
        await outbox.WriteAsync(SampleEnvelope());

        var publisher = new FakePublisher(failWhen: _ => true);
        var delay = new RecordingDelay();
        // step 100, cap 250: 100, 200, then 300→capped to 250, 400→250.
        var relay = new OutboxRelay(publisher.PublishAsync, store, backoffStepMs: 100, backoffCapMs: 250, delay: delay.DelayAsync);

        for (int i = 0; i < 4; i++)
        {
            await relay.FlushAsync();
        }

        Assert.Equal(new[] { 100, 200, 250, 250 }, delay.Delays);
    }

    [Fact]
    public async Task DefaultDelayDoesNotSleepForZeroBackoff()
    {
        // No injected delay → the default Task.Delay path; a successful publish never hits it,
        // proving the relay works end-to-end without a custom sleeper.
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);
        await outbox.WriteAsync(SampleEnvelope());

        var publisher = new FakePublisher();
        var relay = new OutboxRelay(publisher.PublishAsync, store);

        OutboxRelayResult result = await relay.FlushAsync();
        Assert.Equal(1, result.Published);
    }

    // ── relay: argument guards ──────────────────────────────────────────────────────

    [Fact]
    public void RelayRejectsNullPublisherAndStore()
    {
        var store = new InMemoryOutboxStore();
        OutboxPublisher publisher = (_, _, _) => Task.CompletedTask;

        Assert.Throws<ArgumentNullException>(() => new OutboxRelay(null!, store));
        Assert.Throws<ArgumentNullException>(() => new OutboxRelay(publisher, null!));
    }

    // ── in-memory store: contract details ───────────────────────────────────────────

    [Fact]
    public async Task FetchReturnsRowsOldestFirstAndHonorsLimit()
    {
        var store = new InMemoryOutboxStore();
        var outbox = new OutboxWriter(store);
        string first = await outbox.WriteAsync(SampleEnvelope(urn: "urn:babel:orders:1"));
        await outbox.WriteAsync(SampleEnvelope(urn: "urn:babel:orders:2"));
        await outbox.WriteAsync(SampleEnvelope(urn: "urn:babel:orders:3"));

        var limited = await store.FetchUnpublishedAsync(2);
        Assert.Equal(2, limited.Count);
        Assert.Equal(first, limited[0].Id); // oldest first
    }

    [Fact]
    public async Task StoredBytesAreIsolatedFromLaterCallerMutation()
    {
        var store = new InMemoryOutboxStore();
        byte[] encoded = Utf8.GetBytes(EnvelopeCodec.Encode(SampleEnvelope()));
        string id = await store.SaveAsync(encoded, "orders");

        // Mutating the caller's array after save must not corrupt the stored row.
        Array.Clear(encoded, 0, encoded.Length);

        var records = await store.FetchUnpublishedAsync(10);
        OutboxRecord record = Assert.Single(records);
        Assert.Equal(id, record.Id);
        Assert.Contains("urn:babel:orders:created", record.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkPublishedAndFailedAreNoOpsForUnknownIds()
    {
        var store = new InMemoryOutboxStore();

        await store.MarkPublishedAsync(new[] { "missing" });
        await store.MarkFailedAsync("missing", "boom");

        Assert.Equal(0, store.AttemptsOf("missing"));
        Assert.Equal(string.Empty, store.LastErrorOf("missing"));
        Assert.Equal(0, store.PendingCount());
    }

    [Fact]
    public async Task SaveRejectsNullEncodedAndMarkPublishedRejectsNullIds()
    {
        var store = new InMemoryOutboxStore();
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!, "q"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.MarkPublishedAsync(null!));
    }

    [Fact]
    public async Task SaveCoercesNullQueueToDefault()
    {
        var store = new InMemoryOutboxStore();
        await store.SaveAsync(Utf8.GetBytes("{}"), null!);

        var record = Assert.Single(await store.FetchUnpublishedAsync(10));
        Assert.Equal("default", record.Queue);
    }

    [Fact]
    public void OutboxRelayResultExposesAttemptedTotal()
    {
        var result = new OutboxRelayResult(3, 2);
        Assert.Equal(5, result.Attempted);
    }
}
