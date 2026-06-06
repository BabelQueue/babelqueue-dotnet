using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BabelQueue;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// Runs the shared cross-SDK conformance suite (vendored under <c>conformance/</c>,
/// copied to the test output) against this core — the same fixtures every BabelQueue
/// SDK must satisfy. Per-message fields (meta.id, trace_id, meta.created_at) are
/// intrinsically unique and are checked for presence only.
/// </summary>
public class ConformanceTests
{
    private static string SuitePath(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "conformance", relative);

    public static IEnumerable<object[]> Cases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SuitePath("manifest.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return new object[] { c.GetProperty("name").GetString()! };
        }
    }

    [Fact]
    public void ManifestMatchesCoreSchemaVersion()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SuitePath("manifest.json")));
        Assert.Equal(EnvelopeCodec.SchemaVersion, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.NotEmpty(doc.RootElement.GetProperty("cases").EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Conformance(string caseName)
    {
        var c = FindCase(caseName);
        var body = File.ReadAllText(SuitePath(c.GetProperty("file").GetString()!));
        var env = EnvelopeCodec.Decode(body);

        if (!c.GetProperty("valid").GetBoolean())
        {
            var reason = c.TryGetProperty("reason", out var r) ? r.GetString() : "";
            Assert.False(EnvelopeCodec.Accepts(env), $"invalid fixture must be rejected ({reason})");
            return;
        }

        Assert.True(EnvelopeCodec.Accepts(env), "valid fixture must be accepted");

        var expect = c.GetProperty("expect");
        Assert.Equal(expect.GetProperty("urn").GetString(), EnvelopeCodec.Urn(env));
        Assert.Equal(expect.GetProperty("attempts").GetInt32(), env.Attempts);
        Assert.Equal(expect.GetProperty("lang").GetString(), env.Meta!.Lang);
        Assert.Equal(expect.GetProperty("schema_version").GetInt32(), env.Meta.SchemaVersion);

        if (expect.TryGetProperty("data", out var dataElement))
        {
            Assert.True(JsonValueEquals(env.Data, ToValue(dataElement)), "data mismatch");
        }

        Assert.False(string.IsNullOrEmpty(env.TraceId));
        Assert.False(string.IsNullOrEmpty(env.Meta.Id));
        Assert.NotEqual(0, env.Meta.CreatedAt);

        if (expect.TryGetProperty("dead_letter", out var dlElement))
        {
            Assert.NotNull(env.DeadLetter);
            if (dlElement.TryGetProperty("reason", out var reason))
            {
                Assert.Equal(reason.GetString(), env.DeadLetter!.Reason);
            }
            if (dlElement.TryGetProperty("original_queue", out var originalQueue))
            {
                Assert.Equal(originalQueue.GetString(), env.DeadLetter!.OriginalQueue);
            }
        }
    }

    private static JsonElement FindCase(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SuitePath("manifest.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (c.GetProperty("name").GetString() == name)
            {
                return c.Clone();
            }
        }
        throw new InvalidOperationException($"Conformance case not found: {name}");
    }

    private static object? ToValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ToValue(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(ToValue).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static bool JsonValueEquals(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        if (a is IReadOnlyDictionary<string, object?> da && b is IReadOnlyDictionary<string, object?> db)
        {
            if (da.Count != db.Count)
            {
                return false;
            }
            foreach (var kv in da)
            {
                if (!db.TryGetValue(kv.Key, out var bv) || !JsonValueEquals(kv.Value, bv))
                {
                    return false;
                }
            }
            return true;
        }
        if (a is System.Collections.IList la && b is System.Collections.IList lb)
        {
            if (la.Count != lb.Count)
            {
                return false;
            }
            for (var i = 0; i < la.Count; i++)
            {
                if (!JsonValueEquals(la[i], lb[i]))
                {
                    return false;
                }
            }
            return true;
        }
        return a.Equals(b);
    }
}
