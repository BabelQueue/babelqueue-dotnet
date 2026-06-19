using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BabelQueue;
using Xunit;

namespace BabelQueue.Tests;

public sealed class RedriveTests
{
    private sealed class MemoryTransport : Redrive.ITransport
    {
        private readonly Dictionary<string, Queue<string>> _queues = new();
        private readonly string? _failQueue;

        public MemoryTransport(string? failQueue = null) => _failQueue = failQueue;

        public Task<Redrive.Reserved?> PopAsync(string queue)
        {
            if (_queues.TryGetValue(queue, out var q) && q.Count > 0)
            {
                return Task.FromResult<Redrive.Reserved?>(new Redrive.Reserved(q.Dequeue(), null));
            }

            return Task.FromResult<Redrive.Reserved?>(null);
        }

        public Task PublishAsync(string queue, string body)
        {
            if (queue == _failQueue)
            {
                throw new InvalidOperationException("publish refused");
            }

            if (!_queues.TryGetValue(queue, out var q))
            {
                q = new Queue<string>();
                _queues[queue] = q;
            }

            q.Enqueue(body);
            return Task.CompletedTask;
        }

        public Task AckAsync(Redrive.Reserved message) => Task.CompletedTask;

        public int Size(string queue) => _queues.TryGetValue(queue, out var q) ? q.Count : 0;
    }

    private static async Task<Envelope> DeadLetteredAsync(MemoryTransport transport, string dlq, string urn, string originalQueue)
    {
        var env = EnvelopeCodec.Make(urn, new Dictionary<string, object?> { ["order_id"] = 1 }, originalQueue);
        var dead = DeadLetters.Annotate(env, "failed", originalQueue);
        await transport.PublishAsync(dlq, EnvelopeCodec.Encode(dead));
        return dead;
    }

    private static async Task<List<Envelope>> DrainAsync(MemoryTransport transport, string queue)
    {
        var result = new List<Envelope>();
        while (await transport.PopAsync(queue) is { } message)
        {
            result.Add(EnvelopeCodec.Decode(message.Body));
        }

        return result;
    }

    [Fact]
    public async Task RedrivesToSourceAndResets()
    {
        var transport = new MemoryTransport();
        var orig = await DeadLetteredAsync(transport, "orders.dlq", "urn:babel:orders:created", "orders");

        var result = await Redrive.RedriveAsync(transport, "orders.dlq", new Redrive.Options());

        Assert.Equal(1, result.Redriven);
        Assert.Equal(0, result.Skipped);
        var got = await DrainAsync(transport, "orders");
        Assert.Single(got);
        Assert.Null(got[0].DeadLetter);
        Assert.Equal(0, got[0].Attempts);
        Assert.Equal(orig.TraceId, got[0].TraceId);
        Assert.Equal("urn:babel:orders:created", got[0].Job);
        Assert.Equal(0, transport.Size("orders.dlq"));
    }

    [Fact]
    public async Task RedrivesToSandbox()
    {
        var transport = new MemoryTransport();
        await DeadLetteredAsync(transport, "orders.dlq", "urn:babel:orders:created", "orders");

        var result = await Redrive.RedriveAsync(transport, "orders.dlq", new Redrive.Options(ToQueue: "sandbox"));

        Assert.Equal(1, result.Redriven);
        Assert.Equal(0, transport.Size("orders"));
        Assert.Equal(1, transport.Size("sandbox"));
    }

    [Fact]
    public async Task DryRunReportsPlanAndChangesNothing()
    {
        var transport = new MemoryTransport();
        await DeadLetteredAsync(transport, "orders.dlq", "urn:babel:orders:created", "orders");

        var result = await Redrive.RedriveAsync(transport, "orders.dlq", new Redrive.Options(DryRun: true));

        Assert.Equal(0, result.Redriven);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("orders", result.Items[0].To);
        Assert.False(result.Items[0].Redriven);
        Assert.Equal(0, transport.Size("orders"));
        var dlq = await DrainAsync(transport, "orders.dlq");
        Assert.Single(dlq);
        Assert.NotNull(dlq[0].DeadLetter);
    }

    [Fact]
    public async Task SelectRedrivesOnlyMatching()
    {
        var transport = new MemoryTransport();
        await DeadLetteredAsync(transport, "dlq", "urn:babel:orders:created", "orders");
        await DeadLetteredAsync(transport, "dlq", "urn:babel:emails:welcome", "emails");

        var result = await Redrive.RedriveAsync(transport, "dlq",
            new Redrive.Options(Select: e => e.Job == "urn:babel:orders:created"));

        Assert.Equal(1, result.Redriven);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, transport.Size("orders"));
        Assert.Equal(0, transport.Size("emails"));
        Assert.Equal(1, transport.Size("dlq"));
    }

    [Fact]
    public async Task MaxCapsHowManyArePulled()
    {
        var transport = new MemoryTransport();
        for (var i = 0; i < 3; i++)
        {
            await DeadLetteredAsync(transport, "dlq", "urn:babel:orders:created", "orders");
        }

        var result = await Redrive.RedriveAsync(transport, "dlq", new Redrive.Options(Max: 2));

        Assert.Equal(2, result.Redriven);
        Assert.Equal(1, transport.Size("dlq"));
    }

    [Fact]
    public async Task PublishFailureRestoresToDlq()
    {
        var transport = new MemoryTransport(failQueue: "orders");
        await DeadLetteredAsync(transport, "dlq", "urn:babel:orders:created", "orders");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Redrive.RedriveAsync(transport, "dlq", new Redrive.Options()));

        Assert.Equal(1, transport.Size("dlq"));
        Assert.Equal(0, transport.Size("orders"));
    }

    [Fact]
    public async Task UndecodableBodyIsRestored()
    {
        var transport = new MemoryTransport();
        await transport.PublishAsync("dlq", "not-json{{{");

        var result = await Redrive.RedriveAsync(transport, "dlq", new Redrive.Options());

        Assert.Equal(0, result.Redriven);
        Assert.Equal(1, result.Skipped);
        var restored = await transport.PopAsync("dlq");
        Assert.NotNull(restored);
        Assert.Equal("not-json{{{", restored!.Body);
    }

    [Fact]
    public async Task NoDeadLetterFallsBackToMetaQueue()
    {
        var transport = new MemoryTransport();
        var env = EnvelopeCodec.Make("urn:babel:orders:created", queue: "orders");
        await transport.PublishAsync("dlq", EnvelopeCodec.Encode(env));

        var result = await Redrive.RedriveAsync(transport, "dlq", new Redrive.Options());

        Assert.Equal(1, result.Redriven);
        Assert.Equal(1, transport.Size("orders"));
    }
}
