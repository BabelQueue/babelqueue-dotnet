using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BabelQueue.Schema;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// Runs the behaviour sections of the vendored cross-SDK conformance manifest:
/// <c>roundtrip</c> (unknown keys survive decode -> attempts+1 -> encode),
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
        foreach (var testCase in Cases("roundtrip"))
        {
            var name = testCase.GetProperty("name").GetString();
            var raw = File.ReadAllText(SuitePath(testCase.GetProperty("file").GetString()!));

            var envelope = EnvelopeCodec.Decode(raw);
            Assert.True(EnvelopeCodec.Accepts(envelope), $"{name}: fixture must be accepted");

            var reEmitted = EnvelopeCodec.Encode(envelope with { Attempts = envelope.Attempts + 1 });
            using var output = JsonDocument.Parse(reEmitted);

            Assert.Equal(
                testCase.GetProperty("expect_attempts").GetInt32(),
                output.RootElement.GetProperty("attempts").GetInt32());

            if (testCase.TryGetProperty("expect_preserved", out var preserved))
            {
                foreach (var expectation in preserved.EnumerateObject())
                {
                    var actual = JsonPointer.Resolve(output.RootElement, expectation.Name);
                    Assert.True(actual.HasValue, $"{name}: {expectation.Name} was dropped on re-emit");
                    Assert.True(
                        JsonPointer.DeepEquals(expectation.Value, actual!.Value),
                        $"{name}: {expectation.Name} expected {expectation.Value.GetRawText()} "
                            + $"but was {actual.Value.GetRawText()}");
                }
            }
        }
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
        foreach (var testCase in Cases("forbidden_keys"))
        {
            var name = testCase.GetProperty("name").GetString();
            var forbiddenKey = testCase.GetProperty("forbidden_key").GetString()!;
            Assert.Equal("warn", testCase.GetProperty("expect").GetString());
            var raw = File.ReadAllText(SuitePath(testCase.GetProperty("file").GetString()!));

            var warnings = new List<string>();
            var envelope = EnvelopeCodec.Decode(raw, warnings.Add);

            Assert.True(EnvelopeCodec.Accepts(envelope), $"{name}: decode must succeed (warn, not reject)");
            Assert.Contains(warnings, w => w.Contains($"'{forbiddenKey}'", StringComparison.Ordinal));

            using var output = JsonDocument.Parse(EnvelopeCodec.Encode(envelope));
            foreach (var pointer in testCase.GetProperty("expect_absent_after_reencode").EnumerateArray())
            {
                Assert.False(
                    JsonPointer.Resolve(output.RootElement, pointer.GetString()!).HasValue,
                    $"{name}: {pointer.GetString()} must not be re-emitted");
            }
        }
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
