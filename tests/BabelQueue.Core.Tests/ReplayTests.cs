using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BabelQueue;
using Xunit;

namespace BabelQueue.Tests;

public sealed class ReplayTests
{
    /// <summary>A reserved DLQ message that also remembers the out-of-band headers it carries.</summary>
    private sealed record Held(string Body, IReadOnlyDictionary<string, string> Headers);

    /// <summary>
    /// An in-memory transport that carries out-of-band headers (an <see cref="Redrive.IHeaderPublisher"/>),
    /// so a redriven message surfaces the marker it was stamped with.
    /// </summary>
    private sealed class HeaderTransport : Redrive.ITransport, Redrive.IHeaderPublisher
    {
        private readonly Dictionary<string, Queue<Held>> _queues = new();

        public Task<Redrive.Reserved?> PopAsync(string queue)
        {
            if (_queues.TryGetValue(queue, out var q) && q.Count > 0)
            {
                var held = q.Dequeue();
                return Task.FromResult<Redrive.Reserved?>(new Redrive.Reserved(held.Body, held));
            }

            return Task.FromResult<Redrive.Reserved?>(null);
        }

        public Task PublishAsync(string queue, string body)
        {
            Enqueue(queue, new Held(body, new Dictionary<string, string>()));
            return Task.CompletedTask;
        }

        public Task PublishWithHeadersAsync(string queue, string body, IReadOnlyDictionary<string, string> headers)
        {
            Enqueue(queue, new Held(body, headers));
            return Task.CompletedTask;
        }

        public Task AckAsync(Redrive.Reserved message) => Task.CompletedTask;

        public Held Peek(string queue) => _queues[queue].Peek();

        private void Enqueue(string queue, Held held)
        {
            if (!_queues.TryGetValue(queue, out var q))
            {
                q = new Queue<Held>();
                _queues[queue] = q;
            }

            q.Enqueue(held);
        }
    }

    /// <summary>An in-memory transport with NO header capability: bypass must be a no-op.</summary>
    private sealed class PlainTransport : Redrive.ITransport
    {
        private readonly Dictionary<string, Queue<string>> _queues = new();

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

    private static async Task<Envelope> DeadLetteredAsync(Redrive.ITransport transport, string dlq, string urn, string originalQueue)
    {
        var env = EnvelopeCodec.Make(urn, new Dictionary<string, object?> { ["order_id"] = 1 }, originalQueue);
        var dead = DeadLetters.Annotate(env, "failed", originalQueue);
        await transport.PublishAsync(dlq, EnvelopeCodec.Encode(dead));
        return dead;
    }

    [Fact]
    public void IsReplayIsNullAndEmptySafe()
    {
        Assert.False(Replay.IsReplay(null));
        Assert.False(Replay.IsReplay(new Dictionary<string, string>()));
        Assert.False(Replay.IsReplay(new Dictionary<string, string> { [Replay.HeaderReplayBypass] = "" }));
        Assert.True(Replay.IsReplay(new Dictionary<string, string> { [Replay.HeaderReplayBypass] = "1" }));
    }

    [Fact]
    public async Task BypassRunsTheEffectWhenNotAReplay()
    {
        var ran = false;
        await Replay.BypassExternalEffectsAsync(new Dictionary<string, string>(), () =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.True(ran);
    }

    [Fact]
    public async Task BypassSkipsTheEffectOnAReplay()
    {
        var headers = new Dictionary<string, string> { [Replay.HeaderReplayBypass] = "1" };
        var ran = false;
        await Replay.BypassExternalEffectsAsync(headers, () =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.False(ran);
    }

    [Fact]
    public async Task RedriveWithBypassStampsHeaderAndConsumeSkipsEffect()
    {
        var transport = new HeaderTransport();
        await DeadLetteredAsync(transport, "orders.dlq", "urn:babel:orders:created", "orders");

        var result = await Redrive.RedriveAsync(transport, "orders.dlq", new Redrive.Options(Bypass: true));

        Assert.Equal(1, result.Redriven);
        Assert.True(result.Items[0].Bypassed, "the item must be flagged bypassed");

        // The redriven message carries the marker on its out-of-band headers...
        var delivered = transport.Peek("orders");
        Assert.Equal("1", delivered.Headers[Replay.HeaderReplayBypass]);

        // ...and a handler that reads those headers treats the delivery as a replay and skips effects.
        Assert.True(Replay.IsReplay(delivered.Headers));
        var emailed = false;
        await Replay.BypassExternalEffectsAsync(delivered.Headers, () =>
        {
            emailed = true;
            return Task.CompletedTask;
        });
        Assert.False(emailed, "the external side-effect must be skipped on a bypassed replay");
    }

    [Fact]
    public async Task MarkerRidesTheTransportSeamNotTheEncodedEnvelope()
    {
        var transport = new HeaderTransport();
        var original = await DeadLetteredAsync(transport, "orders.dlq", "urn:babel:orders:created", "orders");

        await Redrive.RedriveAsync(transport, "orders.dlq", new Redrive.Options(Bypass: true));

        var delivered = transport.Peek("orders");
        // The header is out of band — exactly like traceparent: present on headers, absent from the body.
        Assert.Equal("1", delivered.Headers[Replay.HeaderReplayBypass]);
        Assert.DoesNotContain(Replay.HeaderReplayBypass, delivered.Body, StringComparison.Ordinal);

        // The frozen envelope is unchanged: schema_version stays 1 and trace_id is preserved (GR-4).
        var env = EnvelopeCodec.Decode(delivered.Body);
        Assert.Equal(1, env.Meta?.SchemaVersion);
        Assert.Equal(original.TraceId, env.TraceId);
        Assert.Null(env.DeadLetter);
        Assert.Equal(0, env.Attempts);
    }

    [Fact]
    public async Task NormalRedriveCarriesNoMarkerAndHandlerIsUnaffected()
    {
        var transport = new HeaderTransport();
        await DeadLetteredAsync(transport, "orders.dlq", "urn:babel:orders:created", "orders");

        var result = await Redrive.RedriveAsync(transport, "orders.dlq", new Redrive.Options());

        Assert.Equal(1, result.Redriven);
        Assert.False(result.Items[0].Bypassed);

        var delivered = transport.Peek("orders");
        Assert.False(delivered.Headers.ContainsKey(Replay.HeaderReplayBypass));
        Assert.False(Replay.IsReplay(delivered.Headers));

        var emailed = false;
        await Replay.BypassExternalEffectsAsync(delivered.Headers, () =>
        {
            emailed = true;
            return Task.CompletedTask;
        });
        Assert.True(emailed, "a normal delivery fires the side-effect");
    }

    [Fact]
    public async Task BypassIsNoOpWithoutHeaderPublisher()
    {
        var transport = new PlainTransport();
        await DeadLetteredAsync(transport, "dlq", "urn:babel:orders:created", "orders");

        var result = await Redrive.RedriveAsync(transport, "dlq", new Redrive.Options(Bypass: true));

        Assert.Equal(1, result.Redriven);
        Assert.False(result.Items[0].Bypassed, "bypass must be a no-op without an IHeaderPublisher");
        Assert.Equal(1, transport.Size("orders"));
    }
}
