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
        Assert.Equal("process orders", activity.DisplayName);
        Assert.Equal(ActivityKind.Consumer, activity.Kind);
        Assert.Equal(Telemetry.TraceIdOf(TraceId), activity.TraceId);
        Assert.Equal(TraceId, activity.GetTagItem("messaging.message.conversation_id"));
        Assert.Equal("orders", activity.GetTagItem("messaging.destination.name"));
        Assert.Equal("urn:babel:orders:created", activity.GetTagItem("babelqueue.urn"));
        Assert.Equal(2, activity.GetTagItem("babelqueue.attempts"));
        Assert.Equal("babelqueue", activity.GetTagItem("messaging.system"));
        Assert.Equal("process", activity.GetTagItem("messaging.operation.type"));
        Assert.Equal("process", activity.GetTagItem("messaging.operation.name"));
        // deprecated transition keys, dual-emitted for one MINOR (ADR-0038 section 5)
        Assert.Equal("process", activity.GetTagItem("messaging.operation"));
        Assert.Equal(2, activity.GetTagItem("messaging.babelqueue.attempts"));
    }

    [Fact]
    public async Task WrapNamesTheBrokerAndConsumerGroup()
    {
        Handler handler = Telemetry.Wrap(_ => Task.CompletedTask, headers: null, system: "kafka", consumerGroup: "billing-workers");

        await handler(MakeEnvelope());

        Activity activity = Assert.Single(_activities);
        Assert.Equal("kafka", activity.GetTagItem("messaging.system"));
        Assert.Equal("billing-workers", activity.GetTagItem("messaging.consumer.group.name"));
    }

    [Fact]
    public async Task WrapWithoutAConsumerGroupWritesNoGroupAttribute()
    {
        Handler handler = Telemetry.Wrap(_ => Task.CompletedTask, headers: null, system: "kafka");

        await handler(MakeEnvelope());

        Assert.Null(Assert.Single(_activities).GetTagItem("messaging.consumer.group.name"));
    }

    [Fact]
    public async Task WrapSetsTheConsumerTagsAtStartSoASamplerSeesThem()
    {
        IEnumerable<KeyValuePair<string, object?>>? seenAtSampling = null;
        using var sampler = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                seenAtSampling = options.Tags?.ToList();
                return ActivitySamplingResult.AllData;
            },
        };
        ActivitySource.AddActivityListener(sampler);

        await Telemetry.Wrap(_ => Task.CompletedTask, headers: null, system: "kafka")(MakeEnvelope());

        Assert.NotNull(seenAtSampling);
        Assert.Contains(seenAtSampling!, t => t.Key == "messaging.system" && (string?)t.Value == "kafka");
        Assert.Contains(seenAtSampling!, t => t.Key == "messaging.destination.name" && (string?)t.Value == "orders");
        Assert.Contains(seenAtSampling!, t => t.Key == "babelqueue.urn");
    }

    [Fact]
    public void TheSemconvPinIsExposed()
    {
        Assert.Equal("1.44.0", Telemetry.SemconvVersion);
        Assert.Equal("https://opentelemetry.io/schemas/1.44.0", Telemetry.SchemaUrl);
    }

    [Fact]
    public async Task ANonUuidTraceIdIsHashedWarnedOncePerDistinctValueAndPreserved()
    {
        string value = "order-42-" + Guid.NewGuid().ToString("N");
        var warnings = new List<string>();
        var listener = new CapturingTraceListener(warnings);
        System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            Handler handler = Telemetry.Wrap(_ => Task.CompletedTask);
            Envelope envelope = EnvelopeCodec.Make("urn:babel:orders:created", null, "orders", value);

            await handler(envelope);
            await handler(envelope);

            Activity first = _activities[0];
            Assert.Equal(Telemetry.TraceIdOf(value), first.TraceId);
            Assert.Equal(value, first.GetTagItem("messaging.message.conversation_id")); // byte for byte
            Assert.Single(warnings, w => w.Contains(value, StringComparison.Ordinal)); // once, not twice
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task AUuidTraceIdIsNotWarnedAbout()
    {
        var warnings = new List<string>();
        var listener = new CapturingTraceListener(warnings);
        System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            await Telemetry.Wrap(_ => Task.CompletedTask)(MakeEnvelope());
            Assert.DoesNotContain(warnings, w => w.Contains("is not a UUID", StringComparison.Ordinal));
        }
        finally
        {
            System.Diagnostics.Trace.Listeners.Remove(listener);
        }
    }

    private sealed class CapturingTraceListener : TraceListener
    {
        private readonly List<string> _sink;

        public CapturingTraceListener(List<string> sink) => _sink = sink;

        public override void Write(string? message) => _sink.Add(message ?? string.Empty);

        public override void WriteLine(string? message) => _sink.Add(message ?? string.Empty);
    }

    [Fact]
    public async Task WrapToleratesAnEnvelopeMissingOptionalMeta()
    {
        var partial = new Envelope("urn:babel:orders:created", TraceId, null, null, 0, null);
        Handler handler = Telemetry.Wrap(_ => Task.CompletedTask);

        await handler(partial);

        Activity activity = Assert.Single(_activities);
        Assert.Equal("process", activity.DisplayName); // no destination: the bare operation name
        Assert.Null(activity.GetTagItem("messaging.destination.name"));
        Assert.Null(activity.GetTagItem("messaging.message.id"));
        Assert.Equal("urn:babel:orders:created", activity.GetTagItem("babelqueue.urn"));
    }

    [Fact]
    public async Task WrapRecordsHandlerErrorAndRethrows()
    {
        Handler handler = Telemetry.Wrap(_ => Task.FromException(new InvalidOperationException("boom")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(MakeEnvelope()));

        Activity activity = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("boom", activity.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, activity.GetTagItem("error.type"));
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
        Assert.Equal("send default", activity.DisplayName);
        Assert.Equal("babelqueue", activity.GetTagItem("messaging.system"));
        Assert.Equal("send", activity.GetTagItem("messaging.operation.type"));
        Assert.Equal("send", activity.GetTagItem("messaging.operation.name"));
        Assert.Equal("default", activity.GetTagItem("messaging.destination.name")); // the queue, never the URN
        Assert.Equal("urn:babel:orders:created", activity.GetTagItem("babelqueue.urn"));
        Assert.Equal("publish", activity.GetTagItem("messaging.operation")); // deprecated, dual-emit
        Assert.NotNull(sent);

        // the published trace_id encodes the producer activity's trace, so a consumer recovers it
        Assert.Equal(Telemetry.UuidOf(activity.TraceId), sent!.TraceId);
        Assert.Equal(activity.TraceId, Telemetry.TraceIdOf(sent.TraceId!));
        Assert.Equal(id, activity.GetTagItem("messaging.message.id"));
        Assert.Equal(sent.TraceId, activity.GetTagItem("messaging.message.conversation_id"));
    }

    [Fact]
    public async Task PublishUsesTheGivenQueueAndSystem()
    {
        await Telemetry.PublishAsync(
            "urn:babel:orders:created",
            null,
            env => Task.FromResult(env.Meta!.Queue),
            queue: "orders.events",
            system: "kafka");

        Activity activity = Assert.Single(_activities);
        Assert.Equal("send orders.events", activity.DisplayName);
        Assert.Equal("kafka", activity.GetTagItem("messaging.system"));
        Assert.Equal("orders.events", activity.GetTagItem("messaging.destination.name"));
    }

    [Fact]
    public async Task PublishToABlankQueueHasNoDestination()
    {
        await Telemetry.PublishAsync("urn:babel:orders:created", null, env => Task.FromResult(env.Meta!.Queue), queue: "  ");

        Activity activity = Assert.Single(_activities);
        Assert.Equal("send", activity.DisplayName);
        Assert.Null(activity.GetTagItem("messaging.destination.name"));
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
        Assert.Equal(typeof(InvalidOperationException).FullName, activity.GetTagItem("error.type"));
        // the envelope was built before the send failed, so its ids are on the span
        Assert.NotNull(activity.GetTagItem("messaging.message.id"));
        Assert.NotNull(activity.GetTagItem("messaging.message.conversation_id"));
    }

    [Fact]
    public async Task PublishWithANullQueueReportsTheDefaultQueueTheEnvelopeCarries()
    {
        Envelope? sent = null;

        await Telemetry.PublishAsync(
            "urn:babel:orders:created",
            null,
            env =>
            {
                sent = env;
                return Task.FromResult(env.Meta!.Id);
            },
            queue: null!);

        Assert.Equal("default", sent!.Meta!.Queue);
        Activity activity = Assert.Single(_activities);
        Assert.Equal("send default", activity.DisplayName);
        Assert.Equal("default", activity.GetTagItem("messaging.destination.name"));
    }

    // ---- binary and source compatibility of the pre-ADR-0038 signatures --------------------------------

    [Fact]
    public void ThePreAdr0038PublishAndWrapSignaturesStillExistAsTheirOwnMethods()
    {
        // An assembly compiled against Core 1.13 binds to these exact parameter lists; if a method with one
        // of them is missing, the call throws MissingMethodException at run time.
        var publishes = typeof(Telemetry).GetMethods().Where(m => m.Name == nameof(Telemetry.PublishAsync)).ToList();
        Assert.Contains(publishes, m => ParameterTypes(m).SequenceEqual(new[]
        {
            "System.String", "System.Collections.Generic.IReadOnlyDictionary`2", "System.Func`2", "System.String",
        }));
        Assert.Contains(publishes, m => ParameterTypes(m).SequenceEqual(new[]
        {
            "System.String", "System.Collections.Generic.IReadOnlyDictionary`2", "System.Collections.Generic.IDictionary`2", "System.Func`2", "System.String",
        }));

        var wraps = typeof(Telemetry).GetMethods().Where(m => m.Name == nameof(Telemetry.Wrap)).ToList();
        Assert.Contains(wraps, m => ParameterTypes(m).SequenceEqual(new[] { "BabelQueue.Handler" }));
        Assert.Contains(wraps, m => ParameterTypes(m).SequenceEqual(new[] { "BabelQueue.Handler", "System.Collections.Generic.IReadOnlyDictionary`2" }));
    }

    [Fact]
    public async Task AnOldPublishSignatureCalledByReflectionRunsWithTheDefaultSystem()
    {
        // what a pre-compiled caller does: bind to the exact old parameter list and invoke it
        var old = typeof(Telemetry).GetMethods()
            .Single(m => m.Name == nameof(Telemetry.PublishAsync) && ParameterTypes(m).SequenceEqual(new[]
            {
                "System.String", "System.Collections.Generic.IReadOnlyDictionary`2", "System.Func`2", "System.String",
            }))
            .MakeGenericMethod(typeof(string));
        Func<Envelope, Task<string>> send = env => Task.FromResult(env.Meta!.Queue!);

        var result = await (Task<string>)old.Invoke(null, new object?[] { "urn:babel:orders:created", null, send, "orders" })!;

        Assert.Equal("orders", result);
        Activity activity = Assert.Single(_activities);
        Assert.Equal("send orders", activity.DisplayName);
        Assert.Equal("babelqueue", activity.GetTagItem("messaging.system"));
    }

    [Fact]
    public async Task AnOldHeaderAwarePublishSignatureCalledByReflectionStillInjectsTheTraceparent()
    {
        var old = typeof(Telemetry).GetMethods()
            .Single(m => m.Name == nameof(Telemetry.PublishAsync) && ParameterTypes(m).SequenceEqual(new[]
            {
                "System.String", "System.Collections.Generic.IReadOnlyDictionary`2", "System.Collections.Generic.IDictionary`2", "System.Func`2", "System.String",
            }))
            .MakeGenericMethod(typeof(string));
        Func<Envelope, Task<string>> send = env => Task.FromResult(env.Meta!.Queue!);
        var carrier = new Dictionary<string, string>();

        await (Task<string>)old.Invoke(null, new object?[] { "urn:babel:orders:created", null, carrier, send, "orders" })!;

        Assert.Contains("traceparent", carrier.Keys);
        Assert.Equal("send orders", Assert.Single(_activities).DisplayName);
    }

    [Fact]
    public async Task EveryCallShapeCompilesWithoutAnAmbiguity()
    {
        // Compiling this method is the CS0121 check. The assertions only show each shape runs with the expected queue and
        // system. Which overload each shape bound to is not asserted here; the reflection tests above only show that the
        // old signatures exist and can be invoked.
        var carrier = new Dictionary<string, string>();
        IReadOnlyDictionary<string, object?>? data = null;
        Func<Envelope, Task<string>> send = env => Task.FromResult(env.Meta!.Queue!);

        Assert.Equal("default", await Telemetry.PublishAsync("urn:babel:orders:created", data, send));
        Assert.Equal("q", await Telemetry.PublishAsync("urn:babel:orders:created", data, send, "q"));
        Assert.Equal("q", await Telemetry.PublishAsync("urn:babel:orders:created", data, send, queue: "q"));
        Assert.Equal("default", await Telemetry.PublishAsync("urn:babel:orders:created", data, send, system: "kafka"));
        Assert.Equal("q", await Telemetry.PublishAsync("urn:babel:orders:created", data, send, "q", "kafka"));
        Assert.Equal("default", await Telemetry.PublishAsync("urn:babel:orders:created", data, carrier, send));
        Assert.Equal("q", await Telemetry.PublishAsync("urn:babel:orders:created", data, carrier, send, "q"));
        Assert.Equal("q", await Telemetry.PublishAsync("urn:babel:orders:created", data, carrier, send, "q", "kafka"));
        Assert.Equal("default", await Telemetry.PublishAsync("urn:babel:orders:created", data, headers: null, send));
        Assert.Equal("default", await Telemetry.PublishAsync("urn:babel:orders:created", data, null, send));

        Handler a = Telemetry.Wrap(_ => Task.CompletedTask);
        Handler b = Telemetry.Wrap(_ => Task.CompletedTask, carrier);
        Handler c = Telemetry.Wrap(_ => Task.CompletedTask, null);
        Handler d = Telemetry.Wrap(_ => Task.CompletedTask, headers: null);
        Handler e = Telemetry.Wrap(_ => Task.CompletedTask, system: "kafka");
        Handler f = Telemetry.Wrap(_ => Task.CompletedTask, carrier, "kafka", "g");
        await Task.WhenAll(new[] { a, b, c, d, e, f }.Select(h => h(MakeEnvelope())));

        // 10 publishes + 6 deliveries; only the system-bearing shapes carry a non-default system
        Assert.Equal(16, _activities.Count);
        Assert.Equal(
            new[] { "kafka", "kafka", "kafka", "kafka", "kafka" },
            _activities.Where(x => (string?)x.GetTagItem("messaging.system") != "babelqueue").Select(x => (string)x.GetTagItem("messaging.system")!));
    }

    private static string[] ParameterTypes(System.Reflection.MethodInfo method) =>
        method.GetParameters().Select(p => (p.ParameterType.IsGenericType ? p.ParameterType.GetGenericTypeDefinition() : p.ParameterType).FullName!).ToArray();
}
