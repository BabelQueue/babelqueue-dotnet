using BabelQueue;
using Xunit;

namespace BabelQueue.Tests;

public class IdempotencyTests
{
    private static Envelope Msg(string? id) =>
        new(
            "urn:babel:orders:created",
            "trace-1",
            new Dictionary<string, object?> { ["order_id"] = 7L },
            new Meta(id, "orders", "dotnet", 1, 1L),
            0,
            null);

    [Fact]
    public async Task RunsAndRemembersOnFirstDelivery()
    {
        var store = new InMemoryStore();
        var calls = 0;
        var handler = Idempotency.Wrap(store, _ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await handler(Msg("m1"));

        Assert.Equal(1, calls);
        Assert.True(store.Seen("m1"));
    }

    [Fact]
    public async Task SkipsRedeliveryOfSameId()
    {
        var store = new InMemoryStore();
        var calls = 0;
        var handler = Idempotency.Wrap(store, _ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await handler(Msg("m1"));
        await handler(Msg("m1")); // redelivery → skipped

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunsAgainForADifferentId()
    {
        var store = new InMemoryStore();
        var calls = 0;
        var handler = Idempotency.Wrap(store, _ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await handler(Msg("m1"));
        await handler(Msg("m2"));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DoesNotRememberWhenHandlerThrows()
    {
        var store = new InMemoryStore();
        var calls = 0;
        var handler = Idempotency.Wrap(store, _ =>
        {
            calls++;
            return Task.FromException(new InvalidOperationException("boom"));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(Msg("m1")));
        Assert.False(store.Seen("m1"));

        // A redelivery runs the handler again — retry works.
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(Msg("m1")));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RunsWhenNoUsableId()
    {
        var store = new InMemoryStore();
        var calls = 0;
        var handler = Idempotency.Wrap(store, _ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await handler(Msg("")); // empty id → cannot dedupe → runs
        await handler(Msg(null)); // no id at all → runs

        Assert.Equal(2, calls);
    }

    [Fact]
    public void ForgetRemovesARememberedId()
    {
        var store = new InMemoryStore();
        store.Remember("m1");
        Assert.True(store.Seen("m1"));

        store.Forget("m1");
        Assert.False(store.Seen("m1"));
    }
}
