using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BabelQueue;
using BabelQueue.Schema;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// Runs the behaviour sections of the vendored cross-SDK conformance manifest:
/// <c>roundtrip</c> (unknown keys survive decode -> attempts+1 [-> dead-letter] -> encode),
/// <c>extension_write</c> (producer <c>meta</c> API), <c>encode_reject</c> (core/forbidden keys
/// are rejected, never dropped),
/// <c>data_shape</c> (<c>data</c> is always an object), <c>forbidden_keys</c> (K-15: warn,
/// drop, never re-emit) and <c>payload_schema_unicode</c> (minLength counts code points).
/// None of them is ever skipped: a missing or empty section fails the run.
/// </summary>
public class BehaviourConformanceTests
{
    private static string SuitePath(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "conformance", relative);

    private static JsonElement Section(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SuitePath("manifest.json")));
        Assert.True(
            doc.RootElement.TryGetProperty(name, out var section),
            $"conformance manifest has no '{name}' section");
        return section.Clone();
    }

    private static List<JsonElement> Cases(string name)
    {
        var cases = Section(name).GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        return cases;
    }

    [Fact]
    public void RoundtripPreservesUnknownKeys()
    {
        var violations = new List<string>();
        foreach (var testCase in Cases("roundtrip"))
        {
            var raw = File.ReadAllText(SuitePath(testCase.GetProperty("file").GetString()!));
            violations.AddRange(ConformanceRunner.Roundtrip(testCase, raw));
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ExtensionWriteIsPreservedAcrossReEmit()
    {
        var violations = new List<string>();
        foreach (var testCase in Cases("extension_write"))
        {
            violations.AddRange(ConformanceRunner.ExtensionWrite(testCase));
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EncodeRejectsCoreAndForbiddenKeys()
    {
        var violations = new List<string>();
        var skipped = new List<string>();
        foreach (var testCase in Cases("encode_reject"))
        {
            // A repeated key cannot be expressed through a single dictionary per call
            // (only Go's entry-at-a-time WithMeta can), so this runner skips it.
            if (testCase.TryGetProperty("meta_entries", out _))
            {
                skipped.Add(testCase.GetProperty("name").GetString()!);
                continue;
            }
            violations.AddRange(ConformanceRunner.EncodeReject(testCase));
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        Assert.Equal(new[] { "reject-repeated-key" }, skipped);
    }

    [Fact]
    public void DataShapeIsAlwaysAnObject()
    {
        var modes = new HashSet<string>();
        foreach (var testCase in Cases("data_shape"))
        {
            var name = testCase.GetProperty("name").GetString();
            var mode = testCase.GetProperty("mode").GetString()!;
            modes.Add(mode);

            if (mode == "encode")
            {
                var data = SchemaJson.Parse(testCase.GetProperty("data").GetRawText())
                    as IReadOnlyDictionary<string, object?>;
                var envelope = EnvelopeCodec.Make(
                    testCase.GetProperty("urn").GetString()!,
                    data,
                    testCase.GetProperty("queue").GetString()!);
                using var output = JsonDocument.Parse(EnvelopeCodec.Encode(envelope));
                var dataJson = JsonSerializer.Serialize(output.RootElement.GetProperty("data"));
                Assert.Equal(testCase.GetProperty("expect_encoded_data_json").GetString(), dataJson);
            }
            else
            {
                Assert.Equal("decode", mode);
                var raw = File.ReadAllText(SuitePath(testCase.GetProperty("file").GetString()!));
                var accepted = EnvelopeCodec.Accepts(EnvelopeCodec.Decode(raw));
                Assert.True(
                    testCase.GetProperty("valid").GetBoolean() == accepted,
                    $"{name}: expected valid={testCase.GetProperty("valid").GetBoolean()}");
            }
        }
        Assert.Contains("encode", modes);
        Assert.Contains("decode", modes);
    }

    [Fact]
    public void ForbiddenKeysWarnAndAreNeverReEmitted()
    {
        var violations = new List<string>();
        foreach (var testCase in Cases("forbidden_keys"))
        {
            var raw = File.ReadAllText(SuitePath(testCase.GetProperty("file").GetString()!));
            violations.AddRange(ConformanceRunner.ForbiddenKey(testCase, raw));
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    // --- The runner must really evaluate the optional 1.4.0 fields: each synthetic case
    // below is built to violate exactly one expectation and must be reported. ---

    private const string BasicFixture =
        "{\"job\":\"urn:acme:a\",\"trace_id\":\"t-1\",\"data\":{\"k\":1},"
        + "\"meta\":{\"id\":\"i\",\"queue\":\"orders\",\"lang\":\"php\",\"schema_version\":1,\"created_at\":1},"
        + "\"attempts\":0}";

    private static JsonElement Synthetic(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void RunnerReportsAnExpectAbsentPointerThatExists()
    {
        var violations = ConformanceRunner.Roundtrip(
            Synthetic("{\"name\":\"x\",\"expect_attempts\":1,\"expect_absent_after_reencode\":[\"/job\"]}"),
            BasicFixture);
        Assert.Contains(violations, v => v.Contains("/job", StringComparison.Ordinal));
    }

    [Fact]
    public void RunnerReportsAnExpectPreservedValueThatDiffers()
    {
        var roundtrip = ConformanceRunner.Roundtrip(
            Synthetic("{\"name\":\"x\",\"expect_attempts\":1,\"expect_preserved\":{\"/job\":\"urn:other\"}}"),
            BasicFixture);
        Assert.Contains(roundtrip, v => v.Contains("/job", StringComparison.Ordinal));

        var forbidden = ConformanceRunner.ForbiddenKey(
            Synthetic("{\"name\":\"x\",\"forbidden_key\":\"/timestamp\",\"expect\":\"warn\","
                + "\"expect_absent_after_reencode\":[],\"expect_preserved\":{\"/data/k\":2}}"),
            BasicFixture.Replace("\"attempts\":0", "\"attempts\":0,\"timestamp\":1"));
        Assert.Contains(forbidden, v => v.Contains("/data/k", StringComparison.Ordinal));
    }

    [Fact]
    public void RunnerReportsAnExpectAbsentPointerThatExistsOnForbiddenKeyCases()
    {
        var violations = ConformanceRunner.ForbiddenKey(
            Synthetic("{\"name\":\"x\",\"forbidden_key\":\"/timestamp\",\"expect\":\"warn\","
                + "\"expect_absent_after_reencode\":[\"/job\"]}"),
            BasicFixture.Replace("\"attempts\":0", "\"attempts\":0,\"timestamp\":1"));
        Assert.Contains(violations, v => v.Contains("/job", StringComparison.Ordinal));
    }

    [Fact]
    public void RunnerEvaluatesTheDeadLetterCopy()
    {
        const string good = "{\"name\":\"x\",\"expect_attempts\":1,"
            + "\"apply_dead_letter\":{\"reason\":\"failed\",\"original_queue\":\"orders\"},"
            + "\"expect_dead_letter\":{\"reason\":\"failed\",\"original_queue\":\"orders\"},"
            + "\"expect_preserved\":{\"/job\":\"urn:acme:a\"}}";
        Assert.Empty(ConformanceRunner.Roundtrip(Synthetic(good), BasicFixture));

        const string wrongCase = "{\"name\":\"x\",\"expect_attempts\":1,"
            + "\"apply_dead_letter\":{\"reason\":\"failed\",\"original_queue\":\"orders\"},"
            + "\"expect_dead_letter\":{\"reason\":\"failed\",\"original_queue\":\"nope\"}}";
        var wrong = ConformanceRunner.Roundtrip(Synthetic(wrongCase), BasicFixture);
        Assert.Contains(wrong, v => v.Contains("original_queue", StringComparison.Ordinal));

        // Without apply_dead_letter there is no dead_letter block; expect_dead_letter is ignored.
        Assert.Empty(ConformanceRunner.Roundtrip(
            Synthetic("{\"name\":\"x\",\"expect_attempts\":1,\"expect_dead_letter\":{\"reason\":\"failed\"}}"),
            BasicFixture));
    }

    [Fact]
    public void MakeMetaEntriesSurviveARoundTrip()
    {
        var made = EnvelopeCodec.Make(
            "urn:acme:a",
            new Dictionary<string, object?> { ["k"] = 1 },
            meta: new Dictionary<string, object?> { ["ext"] = new Dictionary<string, object?> { ["tenant"] = "t-9" } });

        var decoded = EnvelopeCodec.Decode(EnvelopeCodec.Encode(made));
        var reencoded = EnvelopeCodec.Encode(decoded with { Attempts = decoded.Attempts + 1 });

        using var doc = JsonDocument.Parse(reencoded);
        Assert.Equal("t-9", doc.RootElement.GetProperty("meta").GetProperty("ext").GetProperty("tenant").GetString());
    }

    [Theory]
    [InlineData("ts")]
    [InlineData("max_retries")]
    [InlineData("attempts")]
    [InlineData("source")]
    [InlineData("id")]
    [InlineData("queue")]
    [InlineData("lang")]
    [InlineData("schema_version")]
    [InlineData("created_at")]
    public void MakeRejectsCoreAndForbiddenMetaKeys(string key)
    {
        var ex = Assert.Throws<BabelQueueException>(() =>
            EnvelopeCodec.Make("urn:acme:a", meta: new Dictionary<string, object?> { [key] = 1 }));
        Assert.Contains("/meta/" + key, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\u00a0")]
    [InlineData("\u0085")]
    [InlineData("\u2003")]
    public void NonAsciiWhitespaceJobIsNotBlankAndDoesNotFallBackToUrn(string blank)
    {
        var raw = "{\"job\":\"" + blank + "\",\"urn\":\"urn:acme:a\",\"trace_id\":\"t-1\",\"data\":{},"
            + "\"meta\":{\"schema_version\":1},\"attempts\":0}";
        var warnings = new List<string>();

        var envelope = EnvelopeCodec.Decode(raw, warnings.Add);

        Assert.NotEqual("urn:acme:a", envelope.Job);
        Assert.Contains(warnings, w => w.Contains("'/urn'", StringComparison.Ordinal));
    }

    [Fact]
    public void AsciiBlankJobFallsBackToUrn()
    {
        var raw = "{\"job\":\" \\t\\n\",\"urn\":\"urn:acme:a\",\"trace_id\":\"t-1\",\"data\":{},"
            + "\"meta\":{\"schema_version\":1},\"attempts\":0}";
        var warnings = new List<string>();

        var envelope = EnvelopeCodec.Decode(raw, warnings.Add);

        Assert.Equal("urn:acme:a", envelope.Job);
        Assert.Empty(warnings);
    }

    [Fact]
    public void JobAndUrnTogetherKeepJobDropUrnAndWarn()
    {
        var raw = "{\"job\":\"urn:acme:a\",\"urn\":\"urn:acme:other\",\"trace_id\":\"t-1\",\"data\":{},"
            + "\"meta\":{\"schema_version\":1},\"attempts\":0}";
        var warnings = new List<string>();

        var envelope = EnvelopeCodec.Decode(raw, warnings.Add);

        Assert.Equal("urn:acme:a", envelope.Job);
        Assert.Contains(warnings, w => w.Contains("'/urn'", StringComparison.Ordinal));
        Assert.DoesNotContain("\"urn\"", EnvelopeCodec.Encode(envelope), StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerReportsDecodeWarningsOnRoundtrip()
    {
        var violations = ConformanceRunner.Roundtrip(
            Synthetic("{\"name\":\"x\",\"expect_attempts\":1}"),
            BasicFixture.Replace("\"attempts\":0", "\"attempts\":0,\"timestamp\":1"));
        Assert.Contains(violations, v => v.Contains("warning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RunnerReportsAnEncodeThatDidNotReject()
    {
        var violations = ConformanceRunner.EncodeReject(Synthetic(
            "{\"name\":\"x\",\"urn\":\"urn:acme:a\",\"queue\":\"q\",\"data\":{},"
            + "\"meta\":{\"vendor_ok\":1},\"offending_key\":\"/meta/vendor_ok\",\"expect\":\"reject\"}"));
        Assert.NotEmpty(violations);
    }

    [Fact]
    public void UnicodeMinLengthCountsCodePoints()
    {
        var section = Section("payload_schema_unicode");
        var schema = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            SchemaJson.Parse(section.GetProperty("schema").GetRawText()));
        var cases = section.GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(cases);

        foreach (var testCase in cases)
        {
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                SchemaJson.Parse(testCase.GetProperty("data").GetRawText()));
            var expected = testCase.GetProperty("valid").GetBoolean();
            var isValid = PayloadValidator.Validate(schema, data) is null;
            Assert.True(expected == isValid, $"{testCase.GetProperty("name").GetString()}: expected valid={expected}");
        }
    }

    [Fact]
    public void DataArrayIsRejectedOnDecode()
    {
        const string raw = "{\"job\":\"urn:babel:orders:created\",\"trace_id\":\"t-1\",\"data\":[1,2],"
            + "\"meta\":{\"id\":\"i\",\"queue\":\"orders\",\"lang\":\"php\",\"schema_version\":1,\"created_at\":1},"
            + "\"attempts\":0}";

        var envelope = EnvelopeCodec.Decode(raw);

        Assert.Null(envelope.Data);
        Assert.False(EnvelopeCodec.Accepts(envelope));
    }
}

/// <summary>
/// The conformance runners for the behaviour sections. Each method returns the list of
/// violations (empty = the case passes) so the unit tests can feed it synthetic cases and
/// prove the optional fields are evaluated rather than ignored.
/// </summary>
internal static class ConformanceRunner
{
    public static List<string> Roundtrip(JsonElement testCase, string raw)
    {
        var name = testCase.GetProperty("name").GetString();
        var violations = new List<string>();

        var warnings = new List<string>();
        var envelope = EnvelopeCodec.Decode(raw, warnings.Add);
        if (warnings.Count > 0)
        {
            violations.Add($"{name}: decode emitted warning(s): {string.Join(" | ", warnings)}");
        }
        if (!EnvelopeCodec.Accepts(envelope))
        {
            violations.Add($"{name}: fixture must be accepted");
            return violations;
        }

        var next = envelope with { Attempts = envelope.Attempts + 1 };
        var deadLettered = false;
        if (testCase.TryGetProperty("apply_dead_letter", out var apply))
        {
            next = DeadLetters.Annotate(
                next,
                apply.GetProperty("reason").GetString()!,
                apply.GetProperty("original_queue").GetString()!,
                error: "conformance runner",
                exception: "ConformanceRunner");
            deadLettered = true;
        }

        using var output = JsonDocument.Parse(EnvelopeCodec.Encode(next));
        var root = output.RootElement;

        var attempts = root.TryGetProperty("attempts", out var a) && a.ValueKind == JsonValueKind.Number
            ? a.GetInt32()
            : -1;
        var expectedAttempts = testCase.GetProperty("expect_attempts").GetInt32();
        if (attempts != expectedAttempts)
        {
            violations.Add($"{name}: attempts expected {expectedAttempts} but was {attempts}");
        }

        CheckPreserved(name, testCase, root, violations);
        CheckAbsent(name, testCase, root, violations);

        if (deadLettered && testCase.TryGetProperty("expect_dead_letter", out var expectDeadLetter))
        {
            if (!root.TryGetProperty("dead_letter", out var deadLetter) || deadLetter.ValueKind != JsonValueKind.Object)
            {
                violations.Add($"{name}: the dead-letter copy has no dead_letter object");
            }
            else
            {
                foreach (var field in expectDeadLetter.EnumerateObject())
                {
                    if (!deadLetter.TryGetProperty(field.Name, out var actual))
                    {
                        violations.Add($"{name}: dead_letter.{field.Name} is missing");
                    }
                    else if (!JsonPointer.DeepEquals(field.Value, actual))
                    {
                        violations.Add(
                            $"{name}: dead_letter.{field.Name} expected {field.Value.GetRawText()} "
                            + $"but was {actual.GetRawText()}");
                    }
                }
            }
        }
        return violations;
    }

    public static List<string> ForbiddenKey(JsonElement testCase, string raw)
    {
        var name = testCase.GetProperty("name").GetString();
        var forbiddenKey = testCase.GetProperty("forbidden_key").GetString()!;
        var violations = new List<string>();

        if (testCase.GetProperty("expect").GetString() != "warn")
        {
            violations.Add($"{name}: only expect=warn is defined");
        }

        var warnings = new List<string>();
        var envelope = EnvelopeCodec.Decode(raw, warnings.Add);
        if (!EnvelopeCodec.Accepts(envelope))
        {
            violations.Add($"{name}: decode must succeed (warn, not reject)");
            return violations;
        }
        if (!warnings.Any(w => w.Contains($"'{forbiddenKey}'", StringComparison.Ordinal)))
        {
            violations.Add($"{name}: no warning names '{forbiddenKey}' (got: {string.Join(" | ", warnings)})");
        }

        using var output = JsonDocument.Parse(EnvelopeCodec.Encode(envelope));
        CheckAbsent(name, testCase, output.RootElement, violations);
        CheckPreserved(name, testCase, output.RootElement, violations);
        return violations;
    }

    public static List<string> ExtensionWrite(JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString();
        var violations = new List<string>();

        var envelope = EnvelopeCodec.Make(
            testCase.GetProperty("urn").GetString()!,
            ToMap(testCase.GetProperty("data")),
            testCase.GetProperty("queue").GetString()!,
            meta: ToMap(testCase.GetProperty("meta")));

        var encoded = EnvelopeCodec.Encode(envelope);
        using var first = JsonDocument.Parse(encoded);
        CheckExpectEnvelope(name + " (encode)", testCase, first.RootElement, violations);

        var hop = EnvelopeCodec.Decode(encoded);
        using var second = JsonDocument.Parse(EnvelopeCodec.Encode(hop with { Attempts = hop.Attempts + 1 }));
        CheckExpectEnvelope(name + " (re-emit)", testCase, second.RootElement, violations);
        if (second.RootElement.GetProperty("attempts").GetInt32() != 1)
        {
            violations.Add($"{name} (re-emit): /attempts expected 1");
        }
        return violations;
    }

    public static List<string> EncodeReject(JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString();
        var violations = new List<string>();
        string? produced = null;
        BabelQueueException? error = null;

        try
        {
            var envelope = EnvelopeCodec.Make(
                testCase.GetProperty("urn").GetString()!,
                ToMap(testCase.GetProperty("data")),
                testCase.GetProperty("queue").GetString()!,
                meta: testCase.TryGetProperty("meta", out var meta) ? ToMap(meta) : null);

            if (testCase.TryGetProperty("extras_top", out var top))
            {
                envelope = envelope with { Extras = ToExtras(top) };
            }
            if (testCase.TryGetProperty("extras_meta", out var metaExtras))
            {
                envelope = envelope with { Meta = envelope.Meta! with { Extras = ToExtras(metaExtras) } };
            }
            produced = EnvelopeCodec.Encode(envelope);
        }
        catch (BabelQueueException e)
        {
            error = e;
        }

        if (error is null)
        {
            violations.Add($"{name}: encode must reject but produced {produced}");
        }
        else if (testCase.TryGetProperty("offending_key", out var offending)
            && !error.Message.Contains(offending.GetString()!, StringComparison.Ordinal))
        {
            violations.Add($"{name}: error should mention {offending.GetString()} but was: {error.Message}");
        }
        return violations;
    }

    private static void CheckPreserved(string? name, JsonElement testCase, JsonElement root, List<string> violations)
    {
        if (!testCase.TryGetProperty("expect_preserved", out var preserved))
        {
            return;
        }
        CheckPointers(name, preserved, root, violations, "was dropped on re-emit");
    }

    private static void CheckExpectEnvelope(string? name, JsonElement testCase, JsonElement root, List<string> violations)
        => CheckPointers(name, testCase.GetProperty("expect_envelope"), root, violations, "is missing");

    private static void CheckPointers(
        string? name, JsonElement expectations, JsonElement root, List<string> violations, string missing)
    {
        foreach (var expectation in expectations.EnumerateObject())
        {
            var actual = JsonPointer.Resolve(root, expectation.Name);
            if (!actual.HasValue)
            {
                violations.Add($"{name}: {expectation.Name} {missing}");
            }
            else if (!JsonPointer.DeepEquals(expectation.Value, actual.Value))
            {
                violations.Add(
                    $"{name}: {expectation.Name} expected {expectation.Value.GetRawText()} "
                    + $"but was {actual.Value.GetRawText()}");
            }
        }
    }

    private static void CheckAbsent(string? name, JsonElement testCase, JsonElement root, List<string> violations)
    {
        if (!testCase.TryGetProperty("expect_absent_after_reencode", out var absent))
        {
            return;
        }
        foreach (var pointer in absent.EnumerateArray())
        {
            if (JsonPointer.Resolve(root, pointer.GetString()!).HasValue)
            {
                violations.Add($"{name}: {pointer.GetString()} must not be present after re-encode");
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> ToMap(JsonElement element)
        => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(SchemaJson.Parse(element.GetRawText()));

    private static Dictionary<string, JsonElement> ToExtras(JsonElement element)
        => element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
}

/// <summary>RFC 6901 pointer resolution and type-strict JSON deep equality for the runners.</summary>
internal static class JsonPointer
{
    public static JsonElement? Resolve(JsonElement root, string pointer)
    {
        if (pointer.Length == 0)
        {
            return root;
        }
        var current = root;
        foreach (var rawToken in pointer[1..].Split('/'))
        {
            var token = rawToken.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(token, out current))
                {
                    return null;
                }
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(token, out var index) || index < 0 || index >= current.GetArrayLength())
                {
                    return null;
                }
                current = current[index];
            }
            else
            {
                return null;
            }
        }
        return current;
    }

    /// <summary>
    /// Type-strict deep equality: <c>1</c> != <c>1.0</c>, <c>{}</c> != <c>[]</c>, object key
    /// order is ignored, array order matters.
    /// </summary>
    public static bool DeepEquals(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            return false;
        }
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedProps = expected.EnumerateObject().ToList();
                if (expectedProps.Count != actual.EnumerateObject().Count())
                {
                    return false;
                }
                foreach (var prop in expectedProps)
                {
                    if (!actual.TryGetProperty(prop.Name, out var other) || !DeepEquals(prop.Value, other))
                    {
                        return false;
                    }
                }
                return true;
            case JsonValueKind.Array:
                var left = expected.EnumerateArray().ToList();
                var right = actual.EnumerateArray().ToList();
                return left.Count == right.Count && left.Zip(right).All(pair => DeepEquals(pair.First, pair.Second));
            case JsonValueKind.Number:
                return expected.GetRawText() == actual.GetRawText();
            case JsonValueKind.String:
                return expected.GetString() == actual.GetString();
            default:
                return true;
        }
    }
}
