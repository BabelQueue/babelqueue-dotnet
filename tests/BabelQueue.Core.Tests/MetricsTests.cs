using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BabelQueue;
using BabelQueue.Tracing;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// ADR-0039 beyond the conformance cases: instrument identity, the cardinality rule, the observer seam
/// and its isolation. The conformance runner (<see cref="OtelConformanceTests"/>) covers the cases.
/// </summary>
// Shares the "ActivityListener" collection: the static Meter is process-wide, so classes that read its
// measurements must not run in parallel.
[Collection("ActivityListener")]
public sealed class MetricsTests
{
    private static Envelope MakeEnvelope(int attempts = 0, string queue = "orders") =>
        EnvelopeCodec.Make("urn:babel:orders:created", new Dictionary<string, object?> { ["order_id"] = 1 }, queue)
        with
        { Attempts = attempts };

    [Fact]
    public void TheMeterIsNamedBabelQueueAndTheInstrumentsHaveTheAdr0039Identity()
    {
        using var capture = new MetricCapture();

        var byName = capture.Instruments.ToDictionary(i => i.Name);
        Assert.Equal(
            new[] { "babelqueue.dlq.messages", "babelqueue.retries", "messaging.client.consumed.messages", "messaging.client.sent.messages", "messaging.process.duration" },
            byName.Keys.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("s", byName["messaging.process.duration"].Unit);
        Assert.All(
            byName.Values.Where(i => i.Name != "messaging.process.duration"),
            i => Assert.Equal("{message}", i.Unit));
        Assert.All(byName.Values, i => Assert.Equal("BabelQueue", i.Meter.Name));
        Assert.Equal(Telemetry.ActivitySourceName, TelemetryMetrics.MeterName);
    }

    [Fact]
    public void TheAdvisoryBucketBoundariesAreExposed()
    {
        Assert.Equal(
            new[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 },
            TelemetryMetrics.AdvisoryBucketBoundaries);
    }

    [Fact]
    public void TheAdvisoryBucketBoundariesCannotBeMutated()
    {
        Assert.IsNotType<double[]>(TelemetryMetrics.AdvisoryBucketBoundaries);
        var asList = Assert.IsAssignableFrom<IList<double>>(TelemetryMetrics.AdvisoryBucketBoundaries);
        Assert.Throws<NotSupportedException>(() => asList[0] = 60);
        Assert.Equal(0.005, TelemetryMetrics.AdvisoryBucketBoundaries[0]);
    }

    [Fact]
    public void TheInstrumentsAreNotPartOfThePublicSurface()
    {
        // only the *Name constants and the read-only boundaries are public: nothing outside the SDK can Add() to a counter
        var publicMembers = typeof(TelemetryMetrics).GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(
            new[]
            {
                "AdvisoryBucketBoundaries", "ConsumedMessagesName", "DlqMessagesName", "MeterName", "ProcessDurationName",
                "RetriesName", "SentMessagesName", "get_AdvisoryBucketBoundaries",
            }.OrderBy(n => n, StringComparer.Ordinal),
            publicMembers);
    }

    [Fact]
    public async Task ANormalDeliveryRecordsConsumedAndDurationButNoRetry()
    {
        using var capture = new MetricCapture();

        await Telemetry.Wrap(_ => Task.CompletedTask, headers: null, system: "rabbitmq")(MakeEnvelope());

        Assert.Equal(
            new[] { "messaging.client.consumed.messages", "messaging.process.duration" },
            capture.Points.Select(p => p.Metric).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARedeliveryRecordsARetryAndAFailureCarriesErrorTypeOnTheDurationOnly()
    {
        using var capture = new MetricCapture();
        Handler handler = Telemetry.Wrap(_ => Task.FromException(new TimeoutException("slow")), headers: null, system: "rabbitmq");

        await Assert.ThrowsAsync<TimeoutException>(() => handler(MakeEnvelope(attempts: 1)));

        Assert.Single(capture.Points, p => p.Metric == "babelqueue.retries");
        MetricPoint duration = Assert.Single(capture.Points, p => p.Metric == "messaging.process.duration");
        Assert.Equal(typeof(TimeoutException).FullName, duration.Tags["error.type"]);
        Assert.All(
            capture.Points.Where(p => p.Metric != "messaging.process.duration"),
            p => Assert.DoesNotContain("error.type", p.Tags.Keys));
    }

    [Fact]
    public async Task NoMetricPointCarriesAHighCardinalityAttribute()
    {
        using var capture = new MetricCapture();
        await Telemetry.Wrap(_ => Task.CompletedTask, headers: null, system: "kafka", consumerGroup: "g")(MakeEnvelope(attempts: 2));
        await Telemetry.PublishAsync("urn:babel:orders:created", null, e => Task.FromResult(e.Meta!.Id), "orders", "kafka");
        new MetricsDeadLetterObserver("kafka").NotifySafely(new DeadLetterEvent("orders", "orders.dlq", "poison"));

        string[] forbidden =
        {
            "babelqueue.urn", "messaging.message.id", "messaging.message.conversation_id", "babelqueue.attempts",
            "babelqueue.schema_version", "messaging.consumer.group.name", "dead_letter.reason", "trace_id",
        };
        Assert.NotEmpty(capture.Points);
        Assert.All(capture.Points, p => Assert.Empty(p.Tags.Keys.Intersect(forbidden)));
    }

    [Fact]
    public async Task ASentMessageIsCountedForAFailedPublishAndThePublishResultIsUnchanged()
    {
        using var capture = new MetricCapture();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Telemetry.PublishAsync<string>("urn:babel:orders:created", null, _ => Task.FromException<string>(new InvalidOperationException("down")), "orders", "rabbitmq"));
        string ok = await Telemetry.PublishAsync("urn:babel:orders:created", null, _ => Task.FromResult("sent"), "orders", "rabbitmq");

        Assert.Equal("sent", ok);
        Assert.Equal(2, capture.Points.Count(p => p.Metric == "messaging.client.sent.messages"));
        Assert.Single(capture.Points, p => p.Tags.ContainsKey("error.type"));
    }

    [Fact]
    public void TheDeadLetterObserverCountsOneWithSystemQueueAndOperation()
    {
        using var capture = new MetricCapture();

        new MetricsDeadLetterObserver("servicebus").OnDeadLetter(new DeadLetterEvent("orders"));

        MetricPoint point = Assert.Single(capture.Points);
        Assert.Equal("babelqueue.dlq.messages", point.Metric);
        Assert.Equal(1, point.Value);
        Assert.Equal("servicebus", point.Tags["messaging.system"]);
        Assert.Equal("process", point.Tags["messaging.operation.name"]);
        Assert.Equal("orders", point.Tags["messaging.destination.name"]);
        Assert.Equal(3, point.Tags.Count);
    }

    [Fact]
    public void TheDeadLetterObserverWithoutASystemKeepsTheDefault()
    {
        using var capture = new MetricCapture();

        new MetricsDeadLetterObserver().OnDeadLetter(new DeadLetterEvent());

        MetricPoint point = Assert.Single(capture.Points);
        Assert.Equal("babelqueue", point.Tags["messaging.system"]);
        Assert.DoesNotContain("messaging.destination.name", point.Tags.Keys); // no source queue known
    }

    [Fact]
    public void AnObserverThatThrowsNeverEscapesNotifySafely()
    {
        var throwing = new ThrowingObserver();

        var ex = Record.Exception(() => throwing.NotifySafely(new DeadLetterEvent("orders", "orders.dlq", "failed")));

        Assert.Null(ex);
        Assert.Equal(1, throwing.Calls);
    }

    [Fact]
    public void NotifySafelyIgnoresANullObserver()
    {
        IDeadLetterObserver? none = null;

        Assert.Null(Record.Exception(() => none.NotifySafely(new DeadLetterEvent())));
    }

    [Fact]
    public void TheEventDefaultsAreEmptyStrings()
    {
        var empty = new DeadLetterEvent();

        Assert.Equal(string.Empty, empty.Queue);
        Assert.Equal(string.Empty, empty.DeadLetterQueue);
        Assert.Equal(string.Empty, empty.Reason);
    }

    private sealed class ThrowingObserver : IDeadLetterObserver
    {
        public int Calls { get; private set; }

        public void OnDeadLetter(DeadLetterEvent deadLetterEvent)
        {
            Calls++;
            throw new InvalidOperationException("observer bug");
        }
    }
}
