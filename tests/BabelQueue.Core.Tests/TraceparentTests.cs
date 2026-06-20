using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using BabelQueue;
using BabelQueue.Tracing;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// Proves the ADR-0028 (v0.2) W3C <c>traceparent</c> mechanism in-process: an out-of-band header
/// carrier plus an <see cref="ActivityListener"/> show that a consumer span started by the
/// header-aware <see cref="Telemetry.Wrap(Handler, IReadOnlyDictionary{string, string})"/> is a
/// true child of the producer span across a simulated hop, and that the no-header path falls back
/// to the v0.1 <c>trace_id</c> parent (no regression).
/// </summary>
// See the note on TelemetryTests: same process-wide "BabelQueue" ActivitySource listener, so these
// classes share a collection to run sequentially and avoid cross-class activity leakage.
[Collection("ActivityListener")]
public sealed class TraceparentTests : IDisposable
{
    private const string TraceId = "7b3f9c2a-e41d-4f88-9b2a-1c0d5e6f7a8b";

    private readonly List<Activity> _activities = new();
    private readonly ActivityListener _listener;

    public TraceparentTests()
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

    /* -------------------------------------------------------------------- */
    /*  W3C parse/format round-trip                                          */
    /* -------------------------------------------------------------------- */

    [Fact]
    public void FormatThenParseRoundTripsTheSpanContext()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var source = new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded);

        string? traceparent = Traceparent.Format(source);
        Assert.NotNull(traceparent);
        Assert.StartsWith($"00-{traceId.ToHexString()}-{spanId.ToHexString()}-01", traceparent);

        ActivityContext? parsed = Traceparent.Parse(traceparent);
        Assert.NotNull(parsed);
        Assert.Equal(traceId, parsed!.Value.TraceId);
        Assert.Equal(spanId, parsed.Value.SpanId);
        Assert.Equal(ActivityTraceFlags.Recorded, parsed.Value.TraceFlags);
        Assert.True(parsed.Value.IsRemote);
    }

    [Fact]
    public void FormatReturnsNullForAnInvalidContext()
    {
        Assert.Null(Traceparent.Format(default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    // version ff is reserved/forbidden
    [InlineData("ff-00000000000000000000000000000001-0000000000000001-01")]
    // all-zero trace id
    [InlineData("00-00000000000000000000000000000000-0000000000000001-01")]
    // all-zero span id
    [InlineData("00-00000000000000000000000000000001-0000000000000000-01")]
    // version 00 must not carry trailing parts
    [InlineData("00-00000000000000000000000000000001-0000000000000001-01-extra")]
    public void ParseRejectsMissingOrMalformedTraceparent(string? value)
    {
        Assert.Null(Traceparent.Parse(value));
    }

    [Fact]
    public void TracestateIsCarriedThroughInjectAndExtract()
    {
        var ctx = new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded,
            traceState: "vendor=42");

        using var producer = new Activity("publish").SetParentId(
            ctx.TraceId, ctx.SpanId, ctx.TraceFlags);
        producer.TraceStateString = ctx.TraceState;
        producer.Start();

        var carrier = new Dictionary<string, string>(StringComparer.Ordinal);
        Traceparent.Inject(carrier, producer);

        Assert.True(carrier.ContainsKey(Traceparent.HeaderTraceparent));
        Assert.Equal("vendor=42", carrier[Traceparent.HeaderTracestate]);

        ActivityContext? extracted = Traceparent.RemoteParentFromHeaders(carrier);
        Assert.NotNull(extracted);
        Assert.Equal("vendor=42", extracted!.Value.TraceState);

        producer.Stop();
    }

    /* -------------------------------------------------------------------- */
    /*  Inject / extract on the carrier seam                                 */
    /* -------------------------------------------------------------------- */

    [Fact]
    public void InjectWritesNothingWhenNoActivityIsActive()
    {
        Assert.Null(Activity.Current);
        var carrier = new Dictionary<string, string>(StringComparer.Ordinal);

        Traceparent.Inject(carrier);

        Assert.Empty(carrier);
    }

    [Fact]
    public void RemoteParentFromHeadersReturnsNullForAnEmptyOrHeaderlessCarrier()
    {
        Assert.Null(Traceparent.RemoteParentFromHeaders(null));
        Assert.Null(Traceparent.RemoteParentFromHeaders(new Dictionary<string, string>()));
        Assert.Null(Traceparent.RemoteParentFromHeaders(
            new Dictionary<string, string> { ["traceparent"] = "garbage" }));
    }

    /* -------------------------------------------------------------------- */
    /*  Producer → carrier → consumer end-to-end (the parent-child proof)    */
    /* -------------------------------------------------------------------- */

    [Fact]
    public async Task PublishInjectsTraceparentAndConsumerSpanBecomesAChildOfTheProducerSpan()
    {
        // PRODUCER: publish writes the active producer span's traceparent into the carrier and
        // still stamps trace_id (v0.1 belt-and-braces).
        var carrier = new Dictionary<string, string>(StringComparer.Ordinal);
        Envelope? sent = null;
        await Telemetry.PublishAsync(
            "urn:babel:orders:created",
            new Dictionary<string, object?> { ["order_id"] = 7 },
            carrier,
            env =>
            {
                sent = env;
                return Task.FromResult(env.Meta!.Id!);
            });

        Activity producer = Assert.Single(_activities);
        Assert.Equal(ActivityKind.Producer, producer.Kind);
        Assert.True(carrier.ContainsKey(Traceparent.HeaderTraceparent));
        Assert.NotNull(sent);
        // v0.1 path still works: the stamped trace_id encodes the producer trace.
        Assert.Equal(producer.TraceId, Telemetry.TraceIdOf(sent!.TraceId!));

        _activities.Clear();

        // CONSUMER: the delivered carrier makes the process span a true child of the producer.
        Handler handler = Telemetry.Wrap(_ => Task.CompletedTask, carrier);
        await handler(sent);

        Activity consumer = Assert.Single(_activities);
        Assert.Equal(ActivityKind.Consumer, consumer.Kind);
        Assert.Equal(producer.TraceId, consumer.TraceId);
        // The real cross-hop edge: the consumer's parent IS the producer span.
        Assert.Equal(producer.SpanId, consumer.ParentSpanId);
        Assert.True(consumer.HasRemoteParent);
    }

    [Fact]
    public async Task WrapFallsBackToTheTraceIdParentWhenNoTraceparentHeader()
    {
        // No headers at all → v0.1 trace_id-derived parent (ADR-0025 Option 1), no regression.
        Handler handler = Telemetry.Wrap(_ => Task.CompletedTask, headers: null);
        await handler(MakeEnvelope(attempts: 2));

        Activity consumer = Assert.Single(_activities);
        Assert.Equal(ActivityKind.Consumer, consumer.Kind);
        // Same trace as the v0.1 mapping...
        Assert.Equal(Telemetry.TraceIdOf(TraceId), consumer.TraceId);
        // ...but the parent is the trace_id-derived span, not a real producer span.
        Assert.Equal(TraceId, consumer.GetTagItem("messaging.message.conversation_id"));
    }

    [Fact]
    public async Task WrapFallsBackWhenTheCarrierHasNoUsableTraceparent()
    {
        var emptyCarrier = new Dictionary<string, string>(StringComparer.Ordinal);
        Handler handler = Telemetry.Wrap(_ => Task.CompletedTask, emptyCarrier);
        await handler(MakeEnvelope());

        Activity consumer = Assert.Single(_activities);
        // Falls back to the v0.1 trace_id parent rather than a root or remote span.
        Assert.Equal(Telemetry.TraceIdOf(TraceId), consumer.TraceId);
    }

    [Fact]
    public async Task PublishWithoutACarrierStaysV01Only()
    {
        // The headerless overload is unchanged: no carrier, only trace_id propagation.
        Envelope? sent = null;
        await Telemetry.PublishAsync(
            "urn:babel:orders:created",
            null,
            env =>
            {
                sent = env;
                return Task.FromResult(env.Meta!.Id!);
            });

        Activity producer = Assert.Single(_activities);
        Assert.Equal(ActivityKind.Producer, producer.Kind);
        Assert.NotNull(sent);
        Assert.Equal(producer.TraceId, Telemetry.TraceIdOf(sent!.TraceId!));
    }

    [Fact]
    public async Task WrapStillRecordsHandlerErrorOnTheChildSpanAndRethrows()
    {
        var carrier = new Dictionary<string, string>(StringComparer.Ordinal);
        await Telemetry.PublishAsync(
            "urn:babel:orders:created",
            null,
            carrier,
            env => Task.FromResult(env.Meta!.Id!));
        _activities.Clear();

        Handler handler = Telemetry.Wrap(
            _ => Task.FromException(new InvalidOperationException("boom")),
            carrier);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(MakeEnvelope()));

        Activity consumer = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, consumer.Status);
        Assert.Contains(consumer.Events, e => e.Name == "exception");
    }
}
