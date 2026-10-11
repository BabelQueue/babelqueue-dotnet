using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BabelQueue;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// Redrive carries the DLQ message's native per-message metadata (the Kafka record key, contract
/// §6.5) through the optional <see cref="Redrive.IKeyedRepublisher"/>: null stays null, empty stays
/// empty, bytes stay bytes — on the redrive write and on every restore write back to the DLQ.
/// </summary>
public sealed class RedriveKeyTests
{
    private sealed record KeyHandle(byte[]? Key);

    private sealed record Sent(string Queue, string Body, byte[]? Key, bool Keyed);

    private class PlainTransport : Redrive.ITransport
    {
        protected readonly Dictionary<string, Queue<Redrive.Reserved>> Queues = new();
        public readonly List<Sent> Sends = new();
        private readonly string? _failQueue;

        public PlainTransport(string? failQueue = null) => _failQueue = failQueue;

        public void Seed(string queue, string body, byte[]? key)
        {
            if (!Queues.TryGetValue(queue, out var q))
            {
                Queues[queue] = q = new Queue<Redrive.Reserved>();
            }

            q.Enqueue(new Redrive.Reserved(body, new KeyHandle(key)));
        }

        public Task<Redrive.Reserved?> PopAsync(string queue) =>
            Task.FromResult<Redrive.Reserved?>(
                Queues.TryGetValue(queue, out var q) && q.Count > 0 ? q.Dequeue() : null);

        public Task PublishAsync(string queue, string body) => Record(queue, body, null, false);

        public Task AckAsync(Redrive.Reserved message) => Task.CompletedTask;

        public IReadOnlyList<Sent> To(string queue) => Sends.Where(s => s.Queue == queue).ToList();

        protected Task Record(string queue, string body, byte[]? key, bool keyed)
        {
            if (queue == _failQueue)
            {
                throw new InvalidOperationException("publish refused");
            }

            Sends.Add(new Sent(queue, body, key, keyed));
            return Task.CompletedTask;
        }
    }

    private sealed class KeyedTransport : PlainTransport, Redrive.IKeyedRepublisher
    {
        public KeyedTransport(string? failQueue = null) : base(failQueue) { }

        public Task PublishFromAsync(string queue, string body, Redrive.Reserved from) =>
            Record(queue, body, ((KeyHandle)from.Handle!).Key, true);
    }

    private static string DeadBody(string urn, string originalQueue)
    {
        var env = EnvelopeCodec.Make(urn, new Dictionary<string, object?> { ["order_id"] = 1 }, originalQueue);
        return EnvelopeCodec.Encode(DeadLetters.Annotate(env, "failed", originalQueue));
    }

    public static IEnumerable<object?[]> Keys()
    {
        yield return new object?[] { new byte[] { 0x6f, 0x72, 0x64, 0x2d, 0x31 } };
        yield return new object?[] { null };
        yield return new object?[] { Array.Empty<byte>() };
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task RedrivenMessageCarriesTheRecordKey(byte[]? key)
    {
        var transport = new KeyedTransport();
        transport.Seed("orders.dlq", DeadBody("urn:babel:orders:created", "orders"), key);

        var result = await Redrive.RedriveAsync(transport, "orders.dlq", new Redrive.Options());

        Assert.Equal(1, result.Redriven);
        var sent = Assert.Single(transport.To("orders"));
        Assert.True(sent.Keyed);
        Assert.Equal(key, sent.Key);
        Assert.Equal(key is null, sent.Key is null);
        Assert.Null(EnvelopeCodec.Decode(sent.Body).DeadLetter);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task RestorePathsCarryTheRecordKeyBackToTheDlq(byte[]? key)
    {
        // Dry run, unselected, poison body and a failed redrive write all restore to the DLQ.
        var dry = new KeyedTransport();
        dry.Seed("dlq", DeadBody("urn:babel:orders:created", "orders"), key);
        await Redrive.RedriveAsync(dry, "dlq", new Redrive.Options(DryRun: true));
        AssertRestored(dry, key);

        var unselected = new KeyedTransport();
        unselected.Seed("dlq", DeadBody("urn:babel:orders:created", "orders"), key);
        await Redrive.RedriveAsync(unselected, "dlq", new Redrive.Options(Select: _ => false));
        AssertRestored(unselected, key);

        var poison = new KeyedTransport();
        poison.Seed("dlq", "not json at all", key);
        await Redrive.RedriveAsync(poison, "dlq", new Redrive.Options());
        AssertRestored(poison, key);

        var failing = new KeyedTransport(failQueue: "orders");
        failing.Seed("dlq", DeadBody("urn:babel:orders:created", "orders"), key);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Redrive.RedriveAsync(failing, "dlq", new Redrive.Options()));
        AssertRestored(failing, key);
    }

    private static void AssertRestored(KeyedTransport transport, byte[]? key)
    {
        var restored = Assert.Single(transport.To("dlq"));
        Assert.True(restored.Keyed);
        Assert.Equal(key, restored.Key);
        Assert.Equal(key is null, restored.Key is null);
    }

    [Fact]
    public async Task TransportWithoutKeyedRepublisherGetsPlainPublish()
    {
        var transport = new PlainTransport();
        transport.Seed("dlq", DeadBody("urn:babel:orders:created", "orders"), new byte[] { 1, 2, 3 });

        var result = await Redrive.RedriveAsync(transport, "dlq", new Redrive.Options());

        Assert.Equal(1, result.Redriven);
        var sent = Assert.Single(transport.To("orders"));
        Assert.False(sent.Keyed);
        Assert.Null(sent.Key);
    }
}
