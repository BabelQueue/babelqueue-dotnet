using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using BabelQueue;
using BabelQueue.Tracing;
using Xunit;

namespace BabelQueue.Tests;

// Shares the "ActivityListener" collection with TraceparentTests: both register a process-wide
// ActivitySource listener on the same "BabelQueue" source, so they must not run in parallel or
// one class's activities leak into the other's captured list.
[Collection("ActivityListener")]
public sealed class TelemetryTests : IDisposable
{
    private const string TraceId = "7b3f9c2a-e41d-4f88-9b2a-1c0d5e6f7a8b";

    private readonly List<Activity> _activities = new();
    private readonly ActivityListener _listener;

    public TelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => _activities.Add(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    private static Envelope MakeEnvelope(int attempts = 0) =>
        EnvelopeCodec.Make(
            "urn:babel:orders:created",
            new Dictionary<string, object?> { ["order_id"] = 1 },
            "orders",
            TraceId) with
        { Attempts = attempts };

    [Fact]
    public void TraceIdRoundTripsThroughUuid()
    {
        ActivityTraceId tid = Telemetry.TraceIdOf(TraceId);
        Assert.Equal(TraceId, Telemetry.UuidOf(tid));

        // a non-uuid trace_id maps deterministically to a valid, distinct trace id
        Assert.Equal(Telemetry.TraceIdOf("not-a-uuid"), Telemetry.TraceIdOf("not-a-uuid"));
        Assert.NotEqual(tid, Telemetry.TraceIdOf("not-a-uuid"));

        // 32 chars but not hex → not a UUID, so it is hashed (non-zero)
        Assert.NotEqual(default, Telemetry.TraceIdOf(new string('z', 32)));
    }

    [Fact]
    public async Task WrapEmitsConsumerActivityInTheTraceIdTrace()
    {
        bool called = false;
        Handler handler = Telemetry.Wrap(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await handler(MakeEnvelope(attempts: 2));

        Assert.True(called);
        Activity activity = Assert.Single(_activities);
        Assert.Equal("process urn:babel:orders:created", activity.DisplayName);
        Assert.Equal(ActivityKind.Consumer, activity.Kind);
        Assert.Equal(Telemetry.TraceIdOf(TraceId), activity.TraceId);
        Assert.Equal(TraceId, activity.GetTagItem("messaging.message.conversation_id"));
        Assert.Equal("orders", activity.GetTagItem("messaging.destination.name"));
        Assert.Equal(2, activity.GetTagItem("messaging.babelqueue.attempts"));
    }

    [Fact]
    public async Task WrapToleratesAnEnvelopeMissingOptionalMeta()
    {
        var partial = new Envelope("urn:babel:orders:created", TraceId, null, null, 0, null);
        Handler handler = Telemetry.Wrap(_ => Task.CompletedTask);

        await handler(partial);

        Activity activity = Assert.Single(_activities);
        Assert.Equal(string.Empty, activity.GetTagItem("messaging.destination.name"));
        Assert.Equal(string.Empty, activity.GetTagItem("messaging.message.id"));
    }

    [Fact]
    public async Task WrapRecordsHandlerErrorAndRethrows()
    {
        Handler handler = Telemetry.Wrap(_ => Task.FromException(new InvalidOperationException("boom")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(MakeEnvelope()));

        Activity activity = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Contains(activity.Events, e => e.Name == "exception");
    }

    [Fact]
    public async Task PublishStampsTraceIdFromTheProducerActivity()
    {
        Envelope? sent = null;
        string id = await Telemetry.PublishAsync(
            "urn:babel:orders:created",
            new Dictionary<string, object?> { ["order_id"] = 7 },
            env =>
            {
                sent = env;
                return Task.FromResult(env.Meta!.Id!);
            });

        Activity activity = Assert.Single(_activities);
        Assert.Equal(ActivityKind.Producer, activity.Kind);
        Assert.NotNull(sent);

        // the published trace_id encodes the producer activity's trace, so a consumer recovers it
        Assert.Equal(Telemetry.UuidOf(activity.TraceId), sent!.TraceId);
        Assert.Equal(activity.TraceId, Telemetry.TraceIdOf(sent.TraceId!));
        Assert.Equal(id, activity.GetTagItem("messaging.message.id"));
    }

    [Fact]
    public async Task PublishRecordsAFailingSend()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Telemetry.PublishAsync<string>(
                "urn:babel:orders:created",
                null,
                _ => Task.FromException<string>(new InvalidOperationException("send failed"))));

        Activity activity = Assert.Single(_activities);
        Assert.Equal(ActivityKind.Producer, activity.Kind);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }
}
