using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BabelQueue;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// A failed restore write back to the DLQ must hand the record back through the optional
/// <see cref="Redrive.IRewinder"/> (never acknowledging it), and report both errors if the rewind
/// fails too.
/// </summary>
public sealed class RedriveRewindTests
{
    private class FailingTransport : Redrive.ITransport
    {
        private readonly Queue<string> _dlq = new();

        public int Acks { get; private set; }

        public void Seed(string body) => _dlq.Enqueue(body);

        public Task<Redrive.Reserved?> PopAsync(string queue) =>
            Task.FromResult<Redrive.Reserved?>(_dlq.Count > 0 ? new Redrive.Reserved(_dlq.Dequeue(), null) : null);

        public Task PublishAsync(string queue, string body) =>
            throw new InvalidOperationException("broker unavailable");

        public Task AckAsync(Redrive.Reserved message)
        {
            Acks++;
            return Task.CompletedTask;
        }
    }

    private sealed class RewindingTransport : FailingTransport, Redrive.IRewinder
    {
        public List<Redrive.Reserved> Rewound { get; } = new();

        public Exception? RewindError { get; init; }

        public Task RewindAsync(Redrive.Reserved message)
        {
            Rewound.Add(message);
            return RewindError is null ? Task.CompletedTask : Task.FromException(RewindError);
        }
    }

    // Fails publishes to every queue but the DLQ, so the redrive publish fails and the restore works.
    private sealed class WorkQueueDownTransport : Redrive.ITransport, Redrive.IRewinder
    {
        private readonly Queue<string> _dlq = new();

        public int Acks { get; private set; }

        public List<Redrive.Reserved> Rewound { get; } = new();

        public Exception? RewindError { get; init; }

        public void Seed(string body) => _dlq.Enqueue(body);

        public Task<Redrive.Reserved?> PopAsync(string queue) =>
            Task.FromResult<Redrive.Reserved?>(_dlq.Count > 0 ? new Redrive.Reserved(_dlq.Dequeue(), null) : null);

        public Task PublishAsync(string queue, string body) =>
            queue == "dlq"
                ? Task.CompletedTask
                : throw new InvalidOperationException("work queue unavailable");

        public Task AckAsync(Redrive.Reserved message)
        {
            Acks++;
            return Task.CompletedTask;
        }

        public Task RewindAsync(Redrive.Reserved message)
        {
            Rewound.Add(message);
            return RewindError is null ? Task.CompletedTask : Task.FromException(RewindError);
        }
    }

    // Redrives succeed but acknowledging (committing) the first record throws.
    private sealed class AckFailingTransport : Redrive.ITransport, Redrive.IRewinder
    {
        private readonly Queue<string> _dlq = new();

        public List<Redrive.Reserved> Rewound { get; } = new();

        public void Seed(string body) => _dlq.Enqueue(body);

        public Task<Redrive.Reserved?> PopAsync(string queue) =>
            Task.FromResult<Redrive.Reserved?>(_dlq.Count > 0 ? new Redrive.Reserved(_dlq.Dequeue(), null) : null);

        public Task PublishAsync(string queue, string body) => Task.CompletedTask;

        public Task AckAsync(Redrive.Reserved message) =>
            throw new InvalidOperationException("commit failed");

        public Task RewindAsync(Redrive.Reserved message)
        {
            Rewound.Add(message);
            return Task.CompletedTask;
        }
    }

    private static string DeadLetteredBody()
    {
        var env = EnvelopeCodec.Make("urn:babel:orders:created", new Dictionary<string, object?> { ["order_id"] = 1 }, "orders");
        return EnvelopeCodec.Encode(DeadLetters.Annotate(env, "failed", "orders"));
    }

    public static IEnumerable<object[]> Scenarios() => new[]
    {
        new object[] { "dry-run", new Redrive.Options(DryRun: true) },
        new object[] { "not-selected", new Redrive.Options(Select: _ => false) },
        new object[] { "redrive-then-restore", new Redrive.Options() },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task FailedRestoreRewindsAndNeverAcks(string name, Redrive.Options options)
    {
        var transport = new RewindingTransport();
        transport.Seed(DeadLetteredBody());

        await Assert.ThrowsAnyAsync<Exception>(() => Redrive.RedriveAsync(transport, "dlq", options));

        Assert.Single(transport.Rewound);
        Assert.Equal(0, transport.Acks);
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Fact]
    public async Task UndecodableBodyFailedRestoreRewinds()
    {
        var transport = new RewindingTransport();
        transport.Seed("not-json{{{");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Redrive.RedriveAsync(transport, "dlq", new Redrive.Options()));

        Assert.Single(transport.Rewound);
        Assert.Equal(0, transport.Acks);
    }

    [Fact]
    public async Task RewindFailureIsReportedTogetherWithTheRestoreError()
    {
        var rewindError = new InvalidOperationException("rewind failed");
        var transport = new RewindingTransport { RewindError = rewindError };
        transport.Seed(DeadLetteredBody());

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => Redrive.RedriveAsync(transport, "dlq", new Redrive.Options(DryRun: true)));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains(ex.InnerExceptions, e => e.Message == "broker unavailable");
        Assert.Contains(ex.InnerExceptions, e => ReferenceEquals(e, rewindError));
        Assert.Equal(0, transport.Acks);
    }

    [Fact]
    public async Task PublishAndRestoreFailureAreReportedTogether()
    {
        var transport = new RewindingTransport();
        transport.Seed(DeadLetteredBody());

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => Redrive.RedriveAsync(transport, "dlq", new Redrive.Options()));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Single(transport.Rewound);
        Assert.Equal(0, transport.Acks);
    }

    [Fact]
    public async Task PublishRestoreAndRewindFailureAreReportedFlat()
    {
        var rewindError = new InvalidOperationException("rewind failed");
        var transport = new RewindingTransport { RewindError = rewindError };
        transport.Seed(DeadLetteredBody());

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => Redrive.RedriveAsync(transport, "dlq", new Redrive.Options()));

        Assert.Equal(3, ex.InnerExceptions.Count);
        Assert.DoesNotContain(ex.InnerExceptions, e => e is AggregateException);
        Assert.Equal(2, ex.InnerExceptions.Count(e => e.Message == "broker unavailable"));
        Assert.Contains(ex.InnerExceptions, e => ReferenceEquals(e, rewindError));
        Assert.Equal(0, transport.Acks);
    }

    [Fact]
    public async Task TransportWithoutRewinderStillRethrowsAndNeverAcks()
    {
        var transport = new FailingTransport();
        transport.Seed(DeadLetteredBody());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Redrive.RedriveAsync(transport, "dlq", new Redrive.Options(DryRun: true)));

        Assert.Equal(0, transport.Acks);
    }

    [Fact]
    public async Task PublishFailureWithSuccessfulRestoreRewindsTheRemainingBatch()
    {
        var transport = new WorkQueueDownTransport();
        transport.Seed(DeadLetteredBody());
        transport.Seed(DeadLetteredBody());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Redrive.RedriveAsync(transport, "dlq", new Redrive.Options()));

        Assert.Equal("work queue unavailable", ex.Message);
        Assert.Single(transport.Rewound);
        Assert.Equal(1, transport.Acks); // only the restored record
    }

    [Fact]
    public async Task PublishFailureRewindErrorIsReportedTogether()
    {
        var rewindError = new InvalidOperationException("rewind failed");
        var transport = new WorkQueueDownTransport { RewindError = rewindError };
        transport.Seed(DeadLetteredBody());

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => Redrive.RedriveAsync(transport, "dlq", new Redrive.Options()));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains(ex.InnerExceptions, e => e.Message == "work queue unavailable");
        Assert.Contains(ex.InnerExceptions, e => ReferenceEquals(e, rewindError));
    }

    [Fact]
    public async Task CommitFailureAfterSuccessfulRedriveRewindsTheRemainingBatch()
    {
        var transport = new AckFailingTransport();
        transport.Seed(DeadLetteredBody());
        transport.Seed(DeadLetteredBody());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Redrive.RedriveAsync(transport, "dlq", new Redrive.Options()));

        Assert.Equal("commit failed", ex.Message);
        Assert.Single(transport.Rewound);
    }

    [Fact]
    public async Task ThrowingSelectCallbackRewindsTheRemainingBatch()
    {
        var transport = new WorkQueueDownTransport();
        transport.Seed(DeadLetteredBody());
        transport.Seed(DeadLetteredBody());
        var options = new Redrive.Options(Select: _ => throw new InvalidOperationException("select blew up"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Redrive.RedriveAsync(transport, "dlq", options));

        Assert.Equal("select blew up", ex.Message);
        Assert.Single(transport.Rewound);
        Assert.Equal(0, transport.Acks);
    }
}
