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
/// A missing or empty section fails the run. The only skipped case is <c>reject-repeated-key</c>
/// (it needs <c>meta_entries</c>; one dictionary per call cannot repeat a key), which the manifest
/// allows an SDK to skip; <c>EncodeRejectsCoreAndForbiddenKeys</c> asserts exactly that list.
/// </summary>
public class BehaviourConformanceTests
{
    private static string SuitePath(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "conformance", relative);

    private static string ReadFixture(string relative) => File.ReadAllText(SuitePath(relative));

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
        var violations = new List<string>();
        foreach (var testCase in Cases("data_shape"))
        {
            modes.Add(testCase.GetProperty("mode").GetString()!);
            violations.AddRange(ConformanceRunner.DataShape(testCase));
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        Assert.Contains("encode", modes);
        Assert.Contains("decode", modes);
    }

    [Fact]
    public void TrimSectionFollowsTheAsciiWhitespaceRule()
    {
        var violations = new List<string>();
        var modes = new HashSet<string>();
        foreach (var testCase in Cases("trim"))
        {
            modes.Add(testCase.GetProperty("mode").GetString()!);
            violations.AddRange(ConformanceRunner.Trim(testCase, ReadFixture));
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        Assert.Equal(new[] { "accepts", "decode", "make", "reemit" }, modes.OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    public void JobTypeSectionIgnoresANonStringJob()
    {
        var violations = new List<string>();
        foreach (var testCase in Cases("job_type"))
        {
            violations.AddRange(ConformanceRunner.JobType(testCase, ReadFixture));
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TraceparentSectionExtractsTheRemoteParent()
    {
        var violations = new List<string>();
        foreach (var testCase in Cases("traceparent"))
        {
            violations.AddRange(ConformanceRunner.Traceparent(testCase));
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void IdempotencySectionDedupesOnMetaId()
    {
        var violations = new List<string>();
        var section = Section("idempotency");
        violations.AddRange(ConformanceRunner.IdempotencyDedupKey(section.GetProperty("dedup_key"), ReadFixture));
        var sequences = section.GetProperty("sequences").GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(sequences);
        foreach (var sequence in sequences)
        {
            violations.AddRange(ConformanceRunner.IdempotencySequence(sequence));
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void RunnerReportsATrimExpectationThatDiffers()
    {
        var makeCase = Synthetic("{\"name\":\"x\",\"mode\":\"make\",\"urn\":\" urn:acme:a \",\"expect\":{\"job\":\" urn:acme:a \"}}");
        Assert.NotEmpty(ConformanceRunner.Trim(makeCase, ReadFixture));
    }

    [Fact]
    public void RunnerReportsAJobTypeWarningThatWasNotEmitted()
    {
        const string valid =
            "{\"job\":\"urn:acme:a\",\"trace_id\":\"t\",\"data\":{},\"meta\":{\"id\":\"i\",\"queue\":\"q\","
            + "\"lang\":\"php\",\"schema_version\":1,\"created_at\":1},\"attempts\":0}";
        var testCase = Synthetic(
            "{\"name\":\"x\",\"file\":\"f.json\",\"valid\":true,\"expect\":{\"urn\":\"urn:acme:a\"},\"expect_warning\":true}");
        Assert.NotEmpty(ConformanceRunner.JobType(testCase, _ => valid));
    }

    [Fact]
    public void MakeKeepsUnicodeSpacePaddingAndTrimsOnlyTheAsciiSet()
    {
        // ADR-0032: U+00A0, U+2003 and U+3000 are not whitespace; the ASCII set is trimmed.
        Assert.Equal("\u00a0urn:acme:a\u00a0", EnvelopeCodec.Make("\u00a0urn:acme:a\u00a0").Job);
        Assert.Equal("\u2003urn:acme:a\u3000", EnvelopeCodec.Make(" \t\u2003urn:acme:a\u3000\f\n").Job);
        Assert.Equal("\u00a0", EnvelopeCodec.Make("\u00a0").Job);
        Assert.Throws<BabelQueueException>(() => EnvelopeCodec.Make(" \t\n\r\f\u000b"));
        Assert.Equal("t-1", EnvelopeCodec.Make("urn:acme:a", null, "q", "\t t-1\n", null).TraceId);
        Assert.Equal("\u00a0t-1", EnvelopeCodec.Make("urn:acme:a", null, "q", "\u00a0t-1", null).TraceId);
    }

    [Fact]
    public void AsciiTrimIsLinearOnLongWhitespacePadding()
    {
        var padding = new string(' ', 200_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var job = EnvelopeCodec.Make(padding + "urn:acme:a" + padding).Job;
        Assert.Throws<BabelQueueException>(() => EnvelopeCodec.Make(padding));
        sw.Stop();

        Assert.Equal("urn:acme:a", job);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"trim took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void BlankTraceIdIsRejectedOnlyForTheAsciiSet()
    {
        string Body(string traceId) =>
            "{\"job\":\"urn:acme:a\",\"trace_id\":" + JsonSerializer.Serialize(traceId)
            + ",\"data\":{},\"meta\":{\"id\":\"i\",\"queue\":\"q\",\"lang\":\"php\",\"schema_version\":1,\"created_at\":1},\"attempts\":0}";

        Assert.False(EnvelopeCodec.Accepts(EnvelopeCodec.Decode(Body(" \t\n\r\f\u000b"))));
        Assert.True(EnvelopeCodec.Accepts(EnvelopeCodec.Decode(Body("\u00a0"))));
    }

    [Fact]
    public void NonStringJobIsIgnoredWithAWarningAndFallsBackToUrn()
    {
        var warnings = new List<string>();
        var envelope = EnvelopeCodec.Decode(
            "{\"job\":42,\"urn\":\"urn:acme:b\",\"trace_id\":\"t\",\"data\":{},"
            + "\"meta\":{\"id\":\"i\",\"queue\":\"q\",\"lang\":\"php\",\"schema_version\":1,\"created_at\":1}}",
            warnings.Add);
        Assert.Equal("urn:acme:b", EnvelopeCodec.Urn(envelope));
        Assert.Single(warnings);

        var silent = new List<string>();
        EnvelopeCodec.Decode("{\"job\":\" \",\"urn\":\"urn:acme:b\",\"trace_id\":\"t\",\"data\":{}}", silent.Add);
        Assert.Empty(silent);

        Assert.False(EnvelopeCodec.Accepts(EnvelopeCodec.Decode("{\"job\":null,\"trace_id\":\"t\",\"data\":{}}")));
    }

    [Fact]
    public void BlankJobWithANonStringUrnWarnsAboutTheTypeNotAForbiddenKey()
    {
        var warnings = new List<string>();
        var envelope = EnvelopeCodec.Decode("{\"job\":\" \",\"urn\":5,\"trace_id\":\"t\",\"data\":{}}", warnings.Add);

        Assert.False(EnvelopeCodec.Accepts(envelope));
        var warning = Assert.Single(warnings);
        Assert.Contains("not a string", warning);
        Assert.DoesNotContain("forbidden", warning);
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
            queue: "default",
            traceId: null,
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
            EnvelopeCodec.Make("urn:acme:a", null, "default", null, new Dictionary<string, object?> { [key] = 1 }));
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
    public void LegacyFourParameterMakeOverloadStillExists()
    {
        // Binary compatibility: assemblies compiled against Core < 1.11.0 reference this exact
        // signature; adding a parameter (even an optional one) would throw MissingMethodException.
        var legacy = typeof(EnvelopeCodec).GetMethod(
            "Make",
            new[] { typeof(string), typeof(IReadOnlyDictionary<string, object?>), typeof(string), typeof(string) });
        Assert.NotNull(legacy);
        Assert.Equal(typeof(Envelope), legacy!.ReturnType);

        var envelope = (Envelope)legacy.Invoke(null, new object?[] { "urn:acme:a", null, "orders", "t-1" })!;
        Assert.Equal("urn:acme:a", envelope.Job);
        Assert.Equal("t-1", envelope.TraceId);
        Assert.Equal("orders", envelope.Meta!.Queue);
        Assert.Null(envelope.Meta.Extras);
    }

    [Fact]
    public void ShortMakeCallsBindWithoutAmbiguity()
    {
        Assert.Equal("default", EnvelopeCodec.Make("urn:acme:a").Meta!.Queue);
        Assert.Equal("orders", EnvelopeCodec.Make("urn:acme:a", null, "orders").Meta!.Queue);
        Assert.Equal("t-2", EnvelopeCodec.Make("urn:acme:a", traceId: "t-2").TraceId);
    }

    [Fact]
    public void MakeWrapsAnUnserializableMetaValueInBabelQueueException()
    {
        var cyclic = new Dictionary<string, object?>();
        cyclic["self"] = cyclic;

        var ex = Assert.Throws<BabelQueueException>(() =>
            EnvelopeCodec.Make("urn:acme:a", null, "default", null, new Dictionary<string, object?> { ["ext"] = cyclic }));
        Assert.Contains("/meta/ext", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void EncodeReportsUrnInExtrasAsForbiddenNotAsCollision()
    {
        var made = EnvelopeCodec.Make("urn:acme:a");
        var extras = new Dictionary<string, JsonElement> { ["urn"] = JsonSerializer.SerializeToElement("x") };

        var ex = Assert.Throws<BabelQueueException>(() => EnvelopeCodec.Encode(made with { Extras = extras }));
        Assert.Contains("forbidden", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'/urn'", ex.Message, StringComparison.Ordinal);
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

        var violations = new List<string>();
        foreach (var testCase in cases)
        {
            violations.AddRange(ConformanceRunner.PayloadSchemaCase(schema, testCase));
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
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
            traceId: null,
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
                traceId: null,
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

    public static List<string> PayloadSchemaCase(IReadOnlyDictionary<string, object?> schema, JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString();
        var data = SchemaJson.Parse(testCase.GetProperty("data").GetRawText())
            as IReadOnlyDictionary<string, object?>;
        if (data is null)
        {
            return new List<string> { $"{name}: case data is not an object" };
        }
        var expected = testCase.GetProperty("valid").GetBoolean();
        var isValid = PayloadValidator.Validate(schema, data) is null;
        return expected == isValid
            ? new List<string>()
            : new List<string> { $"{name}: expected valid={expected} but the validator said valid={isValid}" };
    }

    public static List<string> DataShape(JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString();
        var mode = testCase.GetProperty("mode").GetString()!;
        var violations = new List<string>();

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
            var expected = testCase.GetProperty("expect_encoded_data_json").GetString();
            if (expected != dataJson)
            {
                violations.Add($"{name}: encoded data expected {expected} but was {dataJson}");
            }
        }
        else if (mode == "decode")
        {
            var raw = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "conformance", testCase.GetProperty("file").GetString()!));
            var accepted = EnvelopeCodec.Accepts(EnvelopeCodec.Decode(raw));
            if (testCase.GetProperty("valid").GetBoolean() != accepted)
            {
                violations.Add($"{name}: expected valid={testCase.GetProperty("valid").GetBoolean()}");
            }
        }
        else
        {
            violations.Add($"{name}: unknown data_shape mode '{mode}'");
        }
        return violations;
    }

    /// <summary>
    /// ADR-0032 trim section. <c>decode raises</c> maps to <c>Accepts(Decode(...)) == false</c>:
    /// this decoder never throws, it returns an envelope the consumer then refuses.
    /// </summary>
    public static List<string> Trim(JsonElement testCase, Func<string, string> readFixture)
    {
        var name = testCase.GetProperty("name").GetString();
        var mode = testCase.GetProperty("mode").GetString();
        switch (mode)
        {
            case "make":
                return TrimMake(name, testCase);
            case "decode":
                return DecodeIdentity(name, testCase, readFixture(testCase.GetProperty("file").GetString()!));
            case "accepts":
            {
                var violations = new List<string>();
                var envelope = EnvelopeCodec.Decode(readFixture(testCase.GetProperty("file").GetString()!));
                var expectAccepts = testCase.GetProperty("expect_accepts").GetBoolean();
                if (EnvelopeCodec.Accepts(envelope) != expectAccepts)
                {
                    violations.Add($"{name}: accepts expected {expectAccepts}");
                }
                if (testCase.GetProperty("expect_decode_ok").GetBoolean() && EnvelopeCodec.Urn(envelope).Length == 0)
                {
                    violations.Add($"{name}: decode must still resolve an identity");
                }
                return violations;
            }
            case "reemit":
                return Roundtrip(testCase, readFixture(testCase.GetProperty("file").GetString()!));
            default:
                return new List<string> { $"{name}: unknown trim mode '{mode}'" };
        }
    }

    private static List<string> TrimMake(string? name, JsonElement testCase)
    {
        var violations = new List<string>();
        var urn = testCase.GetProperty("urn").GetString()!;
        var traceId = testCase.TryGetProperty("trace_id", out var t) ? t.GetString() : null;
        var expect = testCase.GetProperty("expect");

        if (expect.ValueKind == JsonValueKind.String && expect.GetString() == "reject")
        {
            try
            {
                EnvelopeCodec.Make(urn, null, "default", traceId, null);
                violations.Add($"{name}: make must reject a URN that is empty after trimming");
            }
            catch (BabelQueueException)
            {
                // expected
            }
            return violations;
        }

        var made = EnvelopeCodec.Make(urn, null, "default", traceId, null);
        if (expect.TryGetProperty("job", out var job) && made.Job != job.GetString())
        {
            violations.Add($"{name}: job expected {job.GetRawText()} but was {JsonSerializer.Serialize(made.Job)}");
        }
        if (expect.TryGetProperty("trace_id", out var trace) && made.TraceId != trace.GetString())
        {
            violations.Add($"{name}: trace_id expected {trace.GetRawText()} but was {JsonSerializer.Serialize(made.TraceId)}");
        }
        return violations;
    }

    /// <summary>ADR-0032 section 6 job-type section: a non-string <c>job</c> is ignored with a warning.</summary>
    public static List<string> JobType(JsonElement testCase, Func<string, string> readFixture) =>
        DecodeIdentity(
            testCase.GetProperty("name").GetString(),
            testCase,
            readFixture(testCase.GetProperty("file").GetString()!));

    private static List<string> DecodeIdentity(string? name, JsonElement testCase, string raw)
    {
        var violations = new List<string>();
        var warnings = new List<string>();
        var envelope = EnvelopeCodec.Decode(raw, warnings.Add);
        var urn = EnvelopeCodec.Urn(envelope);

        if (!testCase.GetProperty("valid").GetBoolean())
        {
            // The decoder never throws; "decode raises" is a consumer refusal: no usable identity.
            if (urn.Length != 0 || EnvelopeCodec.Accepts(envelope))
            {
                violations.Add($"{name}: decode must not yield a valid identity (got '{urn}')");
            }
            return violations;
        }

        var expectedUrn = testCase.GetProperty("expect").GetProperty("urn").GetString();
        if (envelope.Job != expectedUrn)
        {
            violations.Add($"{name}: identity expected {JsonSerializer.Serialize(expectedUrn)} but was {JsonSerializer.Serialize(envelope.Job)}");
        }
        var expectWarning = testCase.GetProperty("expect_warning").GetBoolean();
        if ((warnings.Count > 0) != expectWarning)
        {
            violations.Add($"{name}: expected warning={expectWarning} but got {warnings.Count} warning(s)");
        }
        return violations;
    }

    /// <summary>
    /// ADR-0034 section 9 retry backoff: <c>none</c> must equal <c>expect_delay_seconds</c> exactly; <c>full</c>
    /// is <c>uniform(0, d(n))</c>, so over a few draws (both extremes included) the delay stays within
    /// <c>expect_delay_range_seconds</c>.
    /// </summary>
    public static List<string> RetryBackoff(JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString();
        var violations = new List<string>();
        var n = testCase.GetProperty("n").GetInt32();
        var jitter = testCase.GetProperty("retry_jitter").GetString()!;

        RetryBackoffPolicy Build(Func<double>? random) => new(
            testCase.GetProperty("retry_backoff").GetDouble(),
            testCase.GetProperty("retry_backoff_multiplier").GetDouble(),
            testCase.GetProperty("retry_backoff_max").GetDouble(),
            jitter,
            random);

        if (jitter == RetryJitter.None)
        {
            var expected = testCase.GetProperty("expect_delay_seconds").GetDouble();
            var actual = Build(null).DelaySeconds(n);
            if (actual != expected)
            {
                violations.Add($"{name}: expected delay {expected} but was {actual}");
            }
            return violations;
        }

        var range = testCase.GetProperty("expect_delay_range_seconds");
        var min = range.GetProperty("min").GetDouble();
        var max = range.GetProperty("max").GetDouble();
        var draws = new[] { 0.0, 0.25, 0.5, 0.999999, 0.123456 };
        foreach (var draw in draws)
        {
            var d = draw;
            var delay = Build(() => d).DelaySeconds(n);
            if (delay < min || delay > max)
            {
                violations.Add($"{name}: delay {delay} (draw {draw}) outside [{min}, {max}]");
            }
        }
        // The default (Random.Shared) source must stay in range too.
        var policy = Build(null);
        for (var i = 0; i < 20; i++)
        {
            var delay = policy.DelaySeconds(n);
            if (delay < min || delay > max)
            {
                violations.Add($"{name}: delay {delay} (default source) outside [{min}, {max}]");
            }
        }
        return violations;
    }

    private static readonly JsonDocumentOptions DeepJson = new() { MaxDepth = 10_100 };

    /// <summary>
    /// ADR-0035 poison DLQ record: a spliced object keeps every original byte, anything else is the
    /// <c>raw</c> wrapper (base64 + <c>raw_encoding</c> for invalid UTF-8); U+FFFD never appears.
    /// </summary>
    public static List<string> PoisonDlq(JsonElement testCase, string originalQueue)
    {
        var name = testCase.GetProperty("name").GetString();
        var violations = new List<string>();
        var bytes = testCase.TryGetProperty("body_base64", out var b64)
            ? Convert.FromBase64String(b64.GetString()!)
            : System.Text.Encoding.UTF8.GetBytes(testCase.GetProperty("body").GetString()!);
        var expect = testCase.GetProperty("expect");
        var shape = expect.GetProperty("shape").GetString()!;

        var deadLetter = "{\"reason\":\"poison\",\"original_queue\":\"" + originalQueue + "\",\"attempts\":0}";
        var record = PoisonRecord.Build(bytes, deadLetter);
        var text = System.Text.Encoding.UTF8.GetString(record);
        if (text.Contains('\uFFFD', StringComparison.Ordinal))
        {
            violations.Add($"{name}: the record holds U+FFFD");
        }

        using var doc = JsonDocument.Parse(record, DeepJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            violations.Add($"{name}: the record is not a JSON object");
            return violations;
        }
        var deadLetters = doc.RootElement.EnumerateObject().Where(p => p.Name == "dead_letter").ToList();
        if (deadLetters.Count != 1)
        {
            violations.Add($"{name}: the record must carry exactly one top-level dead_letter");
            return violations;
        }
        var without = doc.RootElement.EnumerateObject().Where(p => p.Name != "dead_letter").ToList();
        var want = expect.GetProperty("record");

        if (shape == "splice_or_wrapper" && !SameMembers(without, want))
        {
            foreach (var literal in expect.GetProperty("splice_literals").EnumerateArray())
            {
                if (!text.Contains(literal.GetString()!, StringComparison.Ordinal))
                {
                    violations.Add($"{name}: the record does not contain {literal.GetString()} verbatim");
                }
            }
            if (without.Any(p => p.Name is "raw" or "raw_encoding"))
            {
                violations.Add($"{name}: a spliced record has a top-level raw / raw_encoding member");
            }
            var keys = without.Select(p => p.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var wantKeys = expect.GetProperty("splice_keys").EnumerateArray().Select(k => k.GetString()!).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (!keys.SequenceEqual(wantKeys))
            {
                violations.Add($"{name}: top-level members {string.Join(",", keys)}, expected {string.Join(",", wantKeys)}");
            }
            return violations;
        }

        if (!SameMembers(without, want))
        {
            violations.Add($"{name}: the record without dead_letter differs from the expected record: {text}");
        }
        return violations;
    }

    private static bool SameMembers(List<JsonProperty> members, JsonElement want)
    {
        var wantMembers = want.EnumerateObject().ToList();
        return members.Count == wantMembers.Count
            && members.All(m => wantMembers.Count(w => w.Name == m.Name && SameJson(m.Value, w.Value)) == 1);
    }

    private static bool SameJson(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }
        return a.ValueKind switch
        {
            JsonValueKind.Object => SameMembers(a.EnumerateObject().ToList(), b),
            JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength()
                && a.EnumerateArray().Zip(b.EnumerateArray(), SameJson).All(x => x),
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetRawText() == b.GetRawText(),
            _ => true,
        };
    }

    /// <summary>ADR-0028 traceparent extraction: the header wins, else the envelope <c>trace_id</c>.</summary>
    public static List<string> Traceparent(JsonElement testCase)
    {
        var name = testCase.GetProperty("name").GetString();
        var violations = new List<string>();
        if (testCase.GetProperty("mode").GetString() != "extract")
        {
            return new List<string> { $"{name}: unknown traceparent mode" };
        }

        var header = testCase.GetProperty("traceparent");
        var headers = header.ValueKind == JsonValueKind.String
            ? new Dictionary<string, string> { [Tracing.Traceparent.HeaderTraceparent] = header.GetString()! }
            : new Dictionary<string, string>();
        var traceId = testCase.GetProperty("trace_id").GetString()!;
        var expect = testCase.GetProperty("expect");

        var parent = Tracing.Traceparent.RemoteParentFromHeaders(headers);
        string source;
        string traceIdHex;
        string? spanIdHex = null;
        string? flags = null;
        if (parent is { } context)
        {
            source = "traceparent";
            traceIdHex = context.TraceId.ToHexString();
            spanIdHex = context.SpanId.ToHexString();
            flags = ((int)context.TraceFlags).ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            source = "trace_id";
            traceIdHex = Tracing.Telemetry.TraceIdOf(traceId).ToHexString();
        }

        void Check(string field, string? expected, string? actual)
        {
            if (expected != actual)
            {
                violations.Add($"{name}: {field} expected '{expected}' but was '{actual}'");
            }
        }

        Check("source", expect.GetProperty("source").GetString(), source);
        Check("trace_id_hex", expect.GetProperty("trace_id_hex").GetString(), traceIdHex);
        if (expect.TryGetProperty("parent_span_id", out var span))
        {
            Check("parent_span_id", span.GetString(), spanIdHex);
        }
        if (expect.TryGetProperty("flags", out var flagsExpected))
        {
            Check("flags", flagsExpected.GetString(), flags);
        }
        return violations;
    }

    public static List<string> IdempotencyDedupKey(JsonElement dedupKey, Func<string, string> readFixture)
    {
        var violations = new List<string>();
        var envelope = EnvelopeCodec.Decode(readFixture(dedupKey.GetProperty("envelope_file").GetString()!));
        var expected = dedupKey.GetProperty("expected_key").GetString();
        if (dedupKey.GetProperty("key_field").GetString() != "meta.id")
        {
            violations.Add("dedup_key: the dedupe key must be meta.id");
        }
        if (envelope.Meta?.Id != expected)
        {
            violations.Add($"dedup_key: meta.id expected '{expected}' but was '{envelope.Meta?.Id}'");
        }
        return violations;
    }

    public static List<string> IdempotencySequence(JsonElement sequence)
    {
        var name = sequence.GetProperty("name").GetString();
        var violations = new List<string>();
        var store = new InMemoryStore();
        var runs = 0;
        var outcome = "ok";
        var handler = Idempotency.Wrap(store, _ =>
        {
            runs++;
            return outcome == "throw"
                ? throw new InvalidOperationException("handler failed")
                : System.Threading.Tasks.Task.CompletedTask;
        });
        var template = EnvelopeCodec.Make("urn:acme:a");

        var index = 0;
        foreach (var delivery in sequence.GetProperty("deliveries").EnumerateArray())
        {
            index++;
            var id = delivery.GetProperty("meta_id").GetString()!;
            if (delivery.TryGetProperty("forget_before", out var forget) && forget.GetBoolean())
            {
                store.Forget(id);
            }
            outcome = delivery.TryGetProperty("outcome", out var o) ? o.GetString()! : "ok";
            var before = runs;
            var envelope = template with { Meta = template.Meta! with { Id = id } };
            try
            {
                handler(envelope).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException)
            {
                // a throwing handler leaves the id unmarked; the helper rethrows
            }
            var actual = runs > before ? "run" : "skip";
            var expected = delivery.GetProperty("expect_effect").GetString();
            if (actual != expected)
            {
                violations.Add($"{name}: delivery {index} expected '{expected}' but was '{actual}'");
            }
        }

        var expectedEffects = sequence.GetProperty("expected_effects").GetInt32();
        if (runs != expectedEffects)
        {
            violations.Add($"{name}: expected {expectedEffects} effect(s) but the handler ran {runs} time(s)");
        }
        return violations;
    }

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
