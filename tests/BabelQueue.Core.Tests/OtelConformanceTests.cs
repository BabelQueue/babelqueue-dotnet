using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BabelQueue;
using BabelQueue.Tracing;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// Runs the <c>otel</c> sections of the vendored conformance manifest (v1.9.0): <c>span-shape</c>
/// (ADR-0038 span name, kind, status and the exact attribute set), <c>trace-id</c> (ADR-0038 section 7,
/// the non-UUID rule and the OTel mapping) and <c>metrics</c> (ADR-0039 instruments, attributes and
/// recording rules). The <c>.NET</c> specifics are in <see cref="OtelConformanceRunner"/>.
/// </summary>
// All classes that listen to the process-wide "BabelQueue" ActivitySource / Meter share this
// collection so they run one after another and never see each other's activities or measurements.
[Collection("ActivityListener")]
public sealed class OtelConformanceTests
{
    private static JsonElement Section(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "conformance", "manifest.json")));
        return doc.RootElement.GetProperty(name).Clone();
    }

    private static List<JsonElement> Cases(string name)
    {
        var cases = Section(name).GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        return cases;
    }

    [Fact]
    public void SpanShapeSectionMatchesTheAdr0038Table()
    {
        var violations = Cases("span-shape").SelectMany(OtelConformanceRunner.SpanShape).ToList();
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TraceIdSectionPreservesNonUuidValuesAndMapsThemToTheOtelTraceId()
    {
        var violations = Cases("trace-id").SelectMany(OtelConformanceRunner.TraceId).ToList();
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void MetricsSectionMatchesTheAdr0039InstrumentsAndRecordingRules()
    {
        var violations = Cases("metrics").SelectMany(OtelConformanceRunner.Metrics).ToList();
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void IdentityVerdictSectionClassifiesRawBodiesLikeTheDecoder()
    {
        // core-scope section added in conformance v1.7.0 (ADR-0036 decision 4); vendored here with v1.9.0
        var cases = Cases("identity_verdict");

        // the documented gap: still failing (remove it from IdentityVerdictGapNames once the decoder agrees)
        foreach (var gap in cases.Where(c => OtelConformanceRunner.IdentityVerdictGapNames.Contains(c.GetProperty("name").GetString()!)))
        {
            Assert.NotEmpty(OtelConformanceRunner.IdentityVerdict(gap));
        }

        var violations = cases
            .Where(c => !OtelConformanceRunner.IdentityVerdictGapNames.Contains(c.GetProperty("name").GetString()!))
            .SelectMany(OtelConformanceRunner.IdentityVerdict)
            .ToList();
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EverySpanShapeCaseHasABuiltInRunnerMode()
    {
        // A case with a mode the runner does not know would pass silently otherwise.
        Assert.All(Cases("span-shape"), c => Assert.Equal("span", c.GetProperty("mode").GetString()));
        Assert.All(
            Cases("trace-id"),
            c => Assert.Contains(c.GetProperty("mode").GetString(), new[] { "map", "decode", "reemit", "downstream", "make" }));
        Assert.All(
            Cases("metrics"),
            c => Assert.Contains(c.GetProperty("mode").GetString(), new[] { "instrument", "consume", "publish", "dead_letter" }));
    }
}

/// <summary>The runner of the <c>otel</c> manifest sections; also drives the <c>results.json</c> entries.</summary>
internal static class OtelConformanceRunner
{
    private const string Urn = "urn:acme:order.created";

    // The ADR-0038 section 4 attribute keys: a key of this set that a case does not list must not be on the span.
    private static readonly HashSet<string> TableKeys = new(StringComparer.Ordinal)
    {
        "messaging.system", "messaging.operation.type", "messaging.operation.name",
        "messaging.destination.name", "messaging.message.id", "messaging.message.conversation_id",
        "messaging.consumer.group.name", "babelqueue.urn", "babelqueue.schema_version", "babelqueue.attempts",
        "error.type", "messaging.operation", "messaging.babelqueue.attempts",
    };

    // ----------------------------------------------------------------------------------------------
    // span-shape
    // ----------------------------------------------------------------------------------------------

    public static List<string> SpanShape(JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString();
        var violations = new List<string>();
        void Fail(string message) => violations.Add($"{name}: {message}");

        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        var operation = testCase.GetProperty("operation").GetString();
        var expectError = testCase.GetProperty("outcome").GetString() == "error";
        var system = OptionalString(testCase, "system");
        Envelope? produced = null;
        string? givenQueue = null;
        Exception? thrown = null;

        if (operation == "send")
        {
            givenQueue = OptionalString(testCase, "queue") ?? "default"; // absent: the API default
            var envelopeBuilt = !testCase.TryGetProperty("envelope_built", out var built) || built.GetBoolean();
            // The build fails by an SDK-specific means: a data map that cannot be enumerated.
            IReadOnlyDictionary<string, object?>? data = envelopeBuilt ? new Dictionary<string, object?> { ["order_id"] = 1 } : new ThrowingDictionary();
            thrown = Record(() => Telemetry.PublishAsync<string>(
                testCase.GetProperty("urn").GetString()!,
                data,
                headers: null,
                envelope =>
                {
                    produced = envelope;
                    return expectError ? Task.FromException<string>(new InvalidOperationException("send failed")) : Task.FromResult("ok");
                },
                givenQueue,
                system));
        }
        else
        {
            var queue = OptionalString(testCase, "queue");
            var messageId = OptionalString(testCase, "message_id");
            var meta = new Meta(messageId, queue, "php", testCase.GetProperty("schema_version").GetInt32(), 1L);
            var envelope = new Envelope(
                testCase.GetProperty("urn").GetString(),
                testCase.GetProperty("trace_id").GetString(),
                new Dictionary<string, object?>(),
                meta,
                testCase.GetProperty("attempts").GetInt32(),
                null);
            var wrapped = Telemetry.Wrap(
                _ => expectError ? Task.FromException(new InvalidOperationException("handler failed")) : Task.CompletedTask,
                headers: null,
                system,
                OptionalString(testCase, "consumer_group"));
            thrown = Record(() => wrapped(envelope));
        }

        if (expectError != (thrown is not null))
        {
            Fail($"expected the {operation} outcome to be {(expectError ? "an error" : "ok")} but the call {(thrown is null ? "succeeded" : "threw " + thrown.GetType().Name)}");
        }

        if (activities.Count != 1)
        {
            Fail($"expected exactly one span but {activities.Count} were recorded");
            return violations;
        }

        var activity = activities[0];
        var expect = testCase.GetProperty("expect");
        var producedQueue = produced?.Meta?.Queue;
        var producedQueueBlank = IsBlank(producedQueue);

        string Placeholder(string value) => value switch
        {
            "$produced.meta.id" => produced?.Meta?.Id ?? "<no produced envelope>",
            "$produced.trace_id" => produced?.TraceId ?? "<no produced envelope>",
            "$produced.meta.queue" => producedQueue ?? string.Empty,
            _ => value,
        };

        // span name
        var actualName = activity.DisplayName;
        if (expect.TryGetProperty("span_name", out var spanName))
        {
            var want = spanName.GetString()!;
            if (want.EndsWith("$produced.meta.queue", StringComparison.Ordinal))
            {
                want = producedQueueBlank ? want[..^"$produced.meta.queue".Length].TrimEnd() : want.Replace("$produced.meta.queue", producedQueue, StringComparison.Ordinal);
            }
            if (actualName != want)
            {
                Fail($"span name expected '{want}' but was '{actualName}'");
            }
        }
        else
        {
            var oneOf = expect.GetProperty("span_name_one_of").EnumerateArray().Select(e => e.GetString()).ToList();
            if (!oneOf.Contains(actualName))
            {
                Fail($"span name '{actualName}' is not one of [{string.Join(", ", oneOf)}]");
            }
        }

        if (actualName.Contains(Urn, StringComparison.Ordinal))
        {
            Fail("the URN must never be in the span name");
        }

        var wantKind = expect.GetProperty("span_kind").GetString() == "PRODUCER" ? ActivityKind.Producer : ActivityKind.Consumer;
        if (activity.Kind != wantKind)
        {
            Fail($"span kind expected {wantKind} but was {activity.Kind}");
        }

        var wantStatus = expect.GetProperty("status").GetString() == "error" ? ActivityStatusCode.Error : ActivityStatusCode.Unset;
        if (activity.Status != wantStatus)
        {
            Fail($"status expected {wantStatus} but was {activity.Status}");
        }

        var actual = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in activity.TagObjects)
        {
            actual[tag.Key] = tag.Value;
        }

        // expected attributes: exact
        var wanted = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var attribute in expect.GetProperty("attributes").EnumerateObject())
        {
            if (attribute.Value.ValueKind == JsonValueKind.String
                && attribute.Value.GetString() == "$produced.meta.queue"
                && producedQueueBlank)
            {
                continue; // the destination is not written when the produced queue is blank
            }
            wanted[attribute.Name] = attribute.Value;
        }

        foreach (var (key, value) in wanted)
        {
            if (!actual.TryGetValue(key, out var got))
            {
                Fail($"attribute '{key}' is missing");
                continue;
            }
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (got is not (int or long) || Convert.ToInt64(got, System.Globalization.CultureInfo.InvariantCulture) != value.GetInt64())
                {
                    Fail($"attribute '{key}' expected integer {value.GetInt64()} but was {Describe(got)}");
                }
            }
            else if (got is not string text || text != Placeholder(value.GetString()!))
            {
                Fail($"attribute '{key}' expected '{Placeholder(value.GetString()!)}' but was {Describe(got)}");
            }
        }

        var optional = expect.TryGetProperty("optional_attributes", out var optionalElement)
            ? optionalElement.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var errorTypeRequired = expect.TryGetProperty("error_type", out var errorType) && errorType.GetString() == "present";

        foreach (var (key, got) in actual)
        {
            if (wanted.ContainsKey(key))
            {
                continue;
            }
            if (key == "error.type")
            {
                if (!errorTypeRequired)
                {
                    Fail("error.type must not be on a span that did not fail");
                }
                else if (got is not string { Length: > 0 })
                {
                    Fail("error.type must be a non-empty string");
                }
                continue;
            }
            if (optional.Contains(key))
            {
                if (key == "messaging.destination.name" && (got as string != givenQueue || actualName != "send " + givenQueue))
                {
                    Fail($"optional attribute '{key}' is {Describe(got)} but the queue given to the producer is '{givenQueue}' (span name '{actualName}')");
                }
                continue;
            }
            if (TableKeys.Contains(key))
            {
                Fail($"attribute '{key}' must not be on this span");
            }
            else if (key.StartsWith("babelqueue.", StringComparison.Ordinal))
            {
                Fail($"attribute '{key}' is a babelqueue.* key outside the ADR-0038 table");
            }
        }

        if (errorTypeRequired && !actual.ContainsKey("error.type"))
        {
            Fail("error.type is missing on a failed span");
        }

        if (errorTypeRequired && !activity.Events.Any(e => e.Name == "exception"))
        {
            Fail("the error was not recorded as an exception event");
        }

        // optional destination consistency when the case does not pin the name
        if (optional.Contains("messaging.destination.name") && !actual.ContainsKey("messaging.destination.name") && actualName != "send")
        {
            Fail($"span name '{actualName}' disagrees with the absent destination");
        }

        return violations;
    }

    // ----------------------------------------------------------------------------------------------
    // trace-id
    // ----------------------------------------------------------------------------------------------

    public static List<string> TraceId(JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString();
        var violations = new List<string>();
        void Fail(string message) => violations.Add($"{name}: {message}");

        var mode = testCase.GetProperty("mode").GetString();
        var traceId = testCase.GetProperty("trace_id").GetString()!;
        var expect = testCase.GetProperty("expect");

        switch (mode)
        {
            case "map":
            {
                var hex = Telemetry.TraceIdOf(traceId).ToHexString();
                if (hex != expect.GetProperty("trace_id_hex").GetString())
                {
                    Fail($"trace_id_hex expected '{expect.GetProperty("trace_id_hex").GetString()}' but was '{hex}'");
                }

                // independent oracle for the declared mapping
                var oracle = expect.GetProperty("mapping").GetString() == "decode"
                    ? traceId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant()
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(traceId)).AsSpan(0, 16)).ToLowerInvariant();
                if (hex != oracle)
                {
                    Fail($"the {expect.GetProperty("mapping").GetString()} mapping gives '{oracle}' but TraceIdOf gave '{hex}'");
                }
                break;
            }
            case "decode":
            {
                var decoded = EnvelopeCodec.Decode(EnvelopeJson(traceId));
                if (decoded.TraceId != expect.GetProperty("trace_id").GetString())
                {
                    Fail($"decoded trace_id expected '{expect.GetProperty("trace_id").GetString()}' but was '{decoded.TraceId}'");
                }
                break;
            }
            case "reemit":
            {
                var decoded = EnvelopeCodec.Decode(EnvelopeJson(traceId));
                var encoded = EnvelopeCodec.Encode(decoded with { Attempts = decoded.Attempts + 1 });
                using var doc = JsonDocument.Parse(encoded);
                var written = doc.RootElement.GetProperty("trace_id").GetString();
                if (written != expect.GetProperty("trace_id").GetString())
                {
                    Fail($"re-emitted trace_id expected '{expect.GetProperty("trace_id").GetString()}' but was '{written}'");
                }
                break;
            }
            case "downstream":
            {
                var inbound = EnvelopeCodec.Decode(EnvelopeJson(traceId));
                var produced = EnvelopeCodec.Make("urn:acme:next", null, "orders", inbound.TraceId);
                if (produced.TraceId != expect.GetProperty("trace_id").GetString())
                {
                    Fail($"downstream trace_id expected '{expect.GetProperty("trace_id").GetString()}' but was '{produced.TraceId}'");
                }
                break;
            }
            case "make":
            {
                var produced = EnvelopeCodec.Make(testCase.GetProperty("urn").GetString()!, null, "orders", traceId);
                if (produced.TraceId != expect.GetProperty("trace_id").GetString())
                {
                    Fail($"make trace_id expected '{expect.GetProperty("trace_id").GetString()}' but was '{produced.TraceId}'");
                }
                var hex = Telemetry.TraceIdOf(produced.TraceId!).ToHexString();
                if (hex != expect.GetProperty("trace_id_hex").GetString())
                {
                    Fail($"trace_id_hex of the stored value expected '{expect.GetProperty("trace_id_hex").GetString()}' but was '{hex}'");
                }
                break;
            }
            default:
                Fail($"unknown trace-id mode '{mode}'");
                break;
        }

        return violations;
    }

    private static string EnvelopeJson(string traceId) =>
        "{\"job\":\"urn:acme:order.created\",\"trace_id\":" + JsonSerializer.Serialize(traceId)
        + ",\"data\":{},\"meta\":{\"id\":\"0b6f3e1c-5d2a-4c7e-9a41-8f3d2b6c1e90\",\"queue\":\"orders\",\"lang\":\"php\","
        + "\"schema_version\":1,\"created_at\":1761000000000},\"attempts\":0}";

    // ----------------------------------------------------------------------------------------------
    // metrics
    // ----------------------------------------------------------------------------------------------

    public static List<string> Metrics(JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString();
        var mode = testCase.GetProperty("mode").GetString();
        return mode switch
        {
            "instrument" => MetricsInstrument(name!, testCase),
            "consume" or "publish" or "dead_letter" => MetricsScenario(name!, mode, testCase),
            _ => new List<string> { $"{name}: unknown metrics mode '{mode}'" },
        };
    }

    private static List<string> MetricsInstrument(string name, JsonElement testCase)
    {
        var violations = new List<string>();
        var expect = testCase.GetProperty("expect");
        var metric = expect.GetProperty("metric").GetString()!;

        // Collect every point of that metric over a small matrix of scenarios.
        using var capture = new MetricCapture();
        RunConsume(capture, "kafka", "orders", 0, error: false, meter: true);
        RunConsume(capture, "kafka", "orders", 2, error: true, meter: true);
        RunConsume(capture, null, null, 0, error: false, meter: true);
        RunPublish(capture, "kafka", "orders", error: false);
        RunPublish(capture, "kafka", "orders", error: true);
        RunPublish(capture, null, "", error: false);
        new MetricsDeadLetterObserver("kafka").NotifySafely(new DeadLetterEvent("orders", "orders.dlq", "failed"));
        new MetricsDeadLetterObserver().NotifySafely(new DeadLetterEvent("", "orders.dlq", "failed"));

        var instrument = capture.Instruments.FirstOrDefault(i => i.Name == metric);
        if (instrument is null)
        {
            violations.Add($"{name}: instrument '{metric}' does not exist on the 'BabelQueue' meter");
            return violations;
        }

        var wantKind = expect.GetProperty("instrument").GetString();
        var gotKind = instrument is Histogram<double> ? "histogram" : instrument is Counter<long> ? "counter" : instrument.GetType().Name;
        if (gotKind != wantKind)
        {
            violations.Add($"{name}: instrument kind expected {wantKind} but was {gotKind}");
        }
        if (instrument.Unit != expect.GetProperty("unit").GetString())
        {
            violations.Add($"{name}: unit expected '{expect.GetProperty("unit").GetString()}' but was '{instrument.Unit}'");
        }

        var attributes = expect.GetProperty("attributes");
        var always = attributes.GetProperty("always").EnumerateArray().Select(e => e.GetString()!).ToList();
        var whenKnown = attributes.GetProperty("when_destination_known").EnumerateArray().Select(e => e.GetString()!).ToList();
        var onFailure = attributes.TryGetProperty("on_failure", out var failureElement)
            ? failureElement.EnumerateArray().Select(e => e.GetString()!).ToList()
            : new List<string>();
        var forbidden = expect.GetProperty("forbidden_attributes").EnumerateArray().Select(e => e.GetString()!).ToList();
        var allowed = always.Concat(whenKnown).Concat(onFailure).ToHashSet(StringComparer.Ordinal);

        var points = capture.Points.Where(p => p.Metric == metric).ToList();
        if (points.Count == 0)
        {
            violations.Add($"{name}: no point was recorded for '{metric}'");
        }
        foreach (var point in points)
        {
            foreach (var key in always.Where(k => !point.Tags.ContainsKey(k)))
            {
                violations.Add($"{name}: a '{metric}' point lacks the always-attribute '{key}'");
            }
            foreach (var key in point.Tags.Keys.Where(k => !allowed.Contains(k)))
            {
                violations.Add($"{name}: a '{metric}' point carries the attribute '{key}' outside the allowed set");
            }
            foreach (var key in point.Tags.Keys.Where(forbidden.Contains))
            {
                violations.Add($"{name}: a '{metric}' point carries the forbidden attribute '{key}'");
            }
            if (point.Tags.TryGetValue("messaging.operation.name", out var op) && op != expect.GetProperty("operation_name").GetString())
            {
                violations.Add($"{name}: messaging.operation.name expected '{expect.GetProperty("operation_name").GetString()}' but was '{op}'");
            }
        }

        // the destination attribute is written exactly when the destination is known
        if (!points.Any(p => p.Tags.ContainsKey("messaging.destination.name")) && whenKnown.Count > 0)
        {
            violations.Add($"{name}: no '{metric}' point carried the destination although it was known");
        }

        return violations.Distinct().ToList();
    }

    private static List<string> MetricsScenario(string name, string mode, JsonElement testCase)
    {
        var violations = new List<string>();
        var expect = testCase.GetProperty("expect");
        var meter = !testCase.TryGetProperty("meter", out var meterElement) || meterElement.GetBoolean();
        var system = OptionalString(testCase, "system");
        var error = testCase.TryGetProperty("outcome", out var outcome) && outcome.GetString() == "error";

        if (mode == "dead_letter" && !testCase.GetProperty("observer_called").GetBoolean())
        {
            // sdk_test_required: nothing is delivered at observer level, whatever the SDK does; the rule is
            // enforced by the transport tests (the core has no DLQ write point of its own, ADR-0039 section 5.2).
            if (!(testCase.TryGetProperty("sdk_test_required", out var required) && required.GetBoolean()))
            {
                violations.Add($"{name}: observer_called false without sdk_test_required is not runnable at observer level");
            }
            return violations;
        }

        using var capture = new MetricCapture(subscribe: meter);
        Envelope? produced = null;

        switch (mode)
        {
            case "consume":
                RunConsume(
                    capture,
                    system,
                    OptionalString(testCase, "queue"),
                    testCase.GetProperty("attempts").GetInt32(),
                    error,
                    meter);
                break;
            case "publish":
                produced = RunPublish(capture, system, OptionalString(testCase, "queue") ?? "default", error);
                break;
            default:
            {
                var eventElement = testCase.GetProperty("event");
                new MetricsDeadLetterObserver(system).NotifySafely(new DeadLetterEvent(
                    eventElement.GetProperty("queue").GetString()!,
                    eventElement.GetProperty("dead_letter_queue").GetString()!,
                    eventElement.GetProperty("reason").GetString()!));
                break;
            }
        }

        if (!meter && MetricCapture.AnyInstrumentEnabled())
        {
            violations.Add($"{name}: with no listener subscribed the instruments must not be enabled");
        }

        var producedQueue = produced?.Meta?.Queue;
        var wantedRecords = expect.GetProperty("records").EnumerateArray().ToList();
        var unmatched = capture.Points.ToList();

        foreach (var record in wantedRecords)
        {
            var wantMetric = record.GetProperty("metric").GetString()!;
            var wantTags = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var attribute in record.GetProperty("attributes").EnumerateObject())
            {
                var value = attribute.Value.GetString()!;
                if (value == "$produced.meta.queue")
                {
                    if (IsBlank(producedQueue))
                    {
                        continue;
                    }
                    value = producedQueue!;
                }
                wantTags[attribute.Name] = value;
            }
            var wantErrorType = record.TryGetProperty("error_type", out var errorTypeElement) && errorTypeElement.GetString() == "present";
            var kind = record.GetProperty("kind").GetString();

            var index = unmatched.FindIndex(p =>
                p.Metric == wantMetric
                && p.Kind == (kind == "add" ? "add" : "record")
                && SameTags(p.Tags, wantTags, wantErrorType)
                && ValueMatches(p.Value, record.GetProperty("value")));
            if (index < 0)
            {
                violations.Add($"{name}: expected a '{wantMetric}' {kind} point with {Format(wantTags)}{(wantErrorType ? " + error.type" : string.Empty)} but none was recorded");
            }
            else
            {
                unmatched.RemoveAt(index);
            }
        }

        foreach (var extra in unmatched)
        {
            violations.Add($"{name}: unexpected '{extra.Metric}' point {Format(extra.Tags)}");
        }

        return violations;
    }

    private static bool SameTags(Dictionary<string, string> actual, Dictionary<string, string> wanted, bool errorTypePresent)
    {
        var copy = new Dictionary<string, string>(actual, StringComparer.Ordinal);
        if (errorTypePresent)
        {
            if (!copy.Remove("error.type", out var value) || value.Length == 0)
            {
                return false;
            }
        }
        return copy.Count == wanted.Count && copy.All(kv => wanted.TryGetValue(kv.Key, out var w) && w == kv.Value);
    }

    private static bool ValueMatches(double value, JsonElement wanted) =>
        wanted.ValueKind == JsonValueKind.String
            ? double.IsFinite(value) && value >= 0 // "non_negative_seconds"
            : value == wanted.GetDouble();

    private static string Format(Dictionary<string, string> tags) =>
        "{" + string.Join(", ", tags.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")) + "}";

    /// <summary>Drives the consumer wrapper once; the handler fails when <paramref name="error"/> is set.</summary>
    internal static void RunConsume(MetricCapture capture, string? system, string? queue, int attempts, bool error, bool meter)
    {
        _ = capture;
        _ = meter;
        var envelope = new Envelope(
            Urn,
            "7b3f9c2a-e41d-4f88-9b2a-1c0d5e6f7a8b",
            new Dictionary<string, object?>(),
            new Meta("0b6f3e1c-5d2a-4c7e-9a41-8f3d2b6c1e90", queue, "php", 1, 1L),
            attempts,
            null);
        var wrapped = Telemetry.Wrap(
            _ => error ? Task.FromException(new InvalidOperationException("handler failed")) : Task.CompletedTask,
            headers: null,
            system);
        var thrown = Record(() => wrapped(envelope));
        if (error != (thrown is InvalidOperationException))
        {
            throw new InvalidOperationException("the consumer wrapper changed the handler's outcome");
        }
    }

    /// <summary>Drives the producer wrapper once; the transport write fails when <paramref name="error"/> is set.</summary>
    internal static Envelope? RunPublish(MetricCapture capture, string? system, string queue, bool error)
    {
        _ = capture;
        Envelope? produced = null;
        var thrown = Record(() => Telemetry.PublishAsync<string>(
            Urn,
            new Dictionary<string, object?> { ["order_id"] = 1 },
            headers: null,
            envelope =>
            {
                produced = envelope;
                return error ? Task.FromException<string>(new InvalidOperationException("send failed")) : Task.FromResult("ok");
            },
            queue,
            system));
        if (error != (thrown is InvalidOperationException))
        {
            throw new InvalidOperationException("the producer wrapper changed the publish outcome");
        }
        return produced;
    }

    // ----------------------------------------------------------------------------------------------
    // identity_verdict (core scope): the decoder verdict on a raw body
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The two <c>identity_verdict</c> cases (a valid identity next to a wrongly typed known member:
    /// <c>attempts</c> a string, <c>data</c> an array) that the .NET core decoder cannot satisfy today. The
    /// vendored suite expects <c>poison</c>; <see cref="EnvelopeCodec.Decode(string)"/> is deliberately lenient
    /// about member types (it maps them to defaults) and the type check lives in the transport packages (for
    /// example <c>KafkaConsumer.DecodeFailure</c>), so the core says <c>ok</c>. Conformance v1.7.0 added this
    /// section, and this package was pinned to v1.6.0 until the v1.9.0 vendor bump surfaced the gap. It is a
    /// real failure: results.json reports it as <c>fail</c> and the CI completeness gate stays red until
    /// the core decoder parity is decided (reported as KARAR GEREKIYOR). Only the xunit result test tolerates
    /// exactly these two, so the rest of the suite stays usable; it fails if either one starts passing.
    /// </summary>
    public static readonly IReadOnlyList<string> IdentityVerdictGapNames = new[]
    {
        "identity with wrong typed attempts",
        "identity with wrong typed data",
    };

    public static List<string> IdentityVerdict(JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString();
        var body = testCase.GetProperty("body").GetString()!;
        var want = testCase.GetProperty("expect").GetString();

        string verdict;
        try
        {
            var envelope = EnvelopeCodec.Decode(body, static _ => { });
            verdict = EnvelopeCodec.Urn(envelope).Length > 0 ? "ok" : "poison";
        }
        catch (Exception e) when (e is JsonException or BabelQueueException or InvalidOperationException or ArgumentException)
        {
            verdict = "poison";
        }

        return verdict == want
            ? new List<string>()
            : new List<string> { $"{name}: expected verdict '{want}' but the decoder gave '{verdict}'" };
    }

    // ----------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsBlank(string? value) => value is null || value.All(c => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r');

    private static string Describe(object? value) => value is null ? "absent/null" : $"{value.GetType().Name} '{value}'";

    private static Exception? Record(Func<Task> action)
    {
        try
        {
            action().GetAwaiter().GetResult();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>A <c>data</c> map whose enumeration throws, so <see cref="EnvelopeCodec.Make(string, IReadOnlyDictionary{string, object?}?, string, string?)"/> fails before an envelope exists.</summary>
    private sealed class ThrowingDictionary : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => throw new InvalidOperationException("the data map cannot be read");

        public IEnumerable<string> Keys => throw new InvalidOperationException("the data map cannot be read");

        public IEnumerable<object?> Values => throw new InvalidOperationException("the data map cannot be read");

        public int Count => throw new InvalidOperationException("the data map cannot be read");

        public bool ContainsKey(string key) => throw new InvalidOperationException("the data map cannot be read");

        public bool TryGetValue(string key, out object? value) => throw new InvalidOperationException("the data map cannot be read");

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new InvalidOperationException("the data map cannot be read");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>One recorded metric data point.</summary>
internal sealed record MetricPoint(string Metric, string Kind, double Value, Dictionary<string, string> Tags);

/// <summary>An in-memory metric reader over the <c>BabelQueue</c> meter (a <see cref="MeterListener"/>).</summary>
internal sealed class MetricCapture : IDisposable
{
    private readonly MeterListener? _listener;
    private readonly object _gate = new();
    private readonly List<MetricPoint> _points = new();
    private readonly List<Instrument> _instruments = new();

    public MetricCapture(bool subscribe = true)
    {
        if (!subscribe)
        {
            return;
        }

        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == TelemetryMetrics.MeterName)
            {
                lock (_gate)
                {
                    _instruments.Add(instrument);
                }
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, "add", value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, "record", value, tags));
        _listener.Start();
    }

    /// <summary>
    /// Whether any BabelQueue instrument currently has a subscriber. The instruments are internal, so they are
    /// found through a listener that observes the meter without enabling anything.
    /// </summary>
    public static bool AnyInstrumentEnabled()
    {
        var seen = new List<Instrument>();
        using var observer = new MeterListener();
        observer.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == TelemetryMetrics.MeterName)
            {
                seen.Add(instrument);
            }
        };
        observer.Start();
        return seen.Count > 0 && seen.Any(i => i.Enabled);
    }

    public IReadOnlyList<MetricPoint> Points
    {
        get
        {
            lock (_gate)
            {
                return _points.ToList();
            }
        }
    }

    public IReadOnlyList<Instrument> Instruments
    {
        get
        {
            lock (_gate)
            {
                return _instruments.ToList();
            }
        }
    }

    public void Dispose()
    {
        if (_listener is null)
        {
            return;
        }

        // MeterListener.Dispose alone leaves Instrument.Enabled true on net8.0; disable explicitly.
        foreach (var instrument in Instruments)
        {
            _listener.DisableMeasurementEvents(instrument);
        }
        _listener.Dispose();
    }

    private void Add(Instrument instrument, string kind, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            map[tag.Key] = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
        lock (_gate)
        {
            _points.Add(new MetricPoint(instrument.Name, kind, value, map));
        }
    }
}
