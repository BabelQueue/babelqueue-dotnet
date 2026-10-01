using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// Unknown-key extras (forward-compatible re-emit) and the forbidden-key policy (K-15)
/// across every re-emit path: plain encode, dead-lettering and redrive.
/// </summary>
public class EnvelopeExtrasTests
{
    private const string WithExtras =
        "{\"job\":\"urn:babel:orders:created\",\"trace_id\":\"t-1\",\"data\":{\"order_id\":1},"
        + "\"meta\":{\"id\":\"i-1\",\"queue\":\"orders\",\"lang\":\"php\",\"schema_version\":1,"
        + "\"created_at\":1749132727000,\"vendor_flag\":true,\"vendor_ctx\":{\"hops\":[1,2]}},"
        + "\"attempts\":3,\"extra_top\":1.0,\"zz_last\":\"x\"}";

    [Fact]
    public void DecodeCapturesUnknownKeysOnly()
    {
        var envelope = EnvelopeCodec.Decode(WithExtras);

        Assert.NotNull(envelope.Extras);
        Assert.Equal(new[] { "extra_top", "zz_last" }, envelope.Extras!.Keys);
        Assert.NotNull(envelope.Meta!.Extras);
        Assert.Equal(new[] { "vendor_flag", "vendor_ctx" }, envelope.Meta.Extras!.Keys);
    }

    [Fact]
    public void CanonicalEnvelopeHasNoExtras()
    {
        var envelope = EnvelopeCodec.Decode(EnvelopeCodec.Encode(EnvelopeCodec.Make("urn:babel:a:b")));

        Assert.Null(envelope.Extras);
        Assert.Null(envelope.Meta!.Extras);
    }

    [Fact]
    public void EncodeWritesCanonicalFieldsFirstThenExtras()
    {
        var json = EnvelopeCodec.Encode(EnvelopeCodec.Decode(WithExtras));

        Assert.True(json.IndexOf("\"attempts\"", StringComparison.Ordinal)
            < json.IndexOf("\"extra_top\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"created_at\"", StringComparison.Ordinal)
            < json.IndexOf("\"vendor_flag\"", StringComparison.Ordinal));
        // The raw number text is preserved (1.0 stays 1.0, never retyped).
        Assert.Contains("\"extra_top\":1.0", json, StringComparison.Ordinal);
        Assert.Contains("\"vendor_ctx\":{\"hops\":[1,2]}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DeadLetteringCarriesExtras()
    {
        var annotated = DeadLetters.Annotate(EnvelopeCodec.Decode(WithExtras), "max_attempts", "orders");

        using var doc = JsonDocument.Parse(EnvelopeCodec.Encode(annotated));
        var root = doc.RootElement;
        Assert.Equal("max_attempts", root.GetProperty("dead_letter").GetProperty("reason").GetString());
        Assert.Equal("x", root.GetProperty("zz_last").GetString());
        Assert.True(root.GetProperty("meta").GetProperty("vendor_flag").GetBoolean());
    }

    [Fact]
    public void RedriveResetCarriesExtras()
    {
        var dead = DeadLetters.Annotate(EnvelopeCodec.Decode(WithExtras), "max_attempts", "orders");

        using var doc = JsonDocument.Parse(EnvelopeCodec.Encode(Redrive.Reset(dead)));
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("attempts").GetInt32());
        Assert.False(root.TryGetProperty("dead_letter", out _));
        Assert.Equal("1.0", root.GetProperty("extra_top").GetRawText());
        Assert.Equal(2, root.GetProperty("meta").GetProperty("vendor_ctx").GetProperty("hops").GetArrayLength());
    }

    [Fact]
    public void ForbiddenKeysAreDroppedWithANamedWarning()
    {
        const string raw =
            "{\"job\":\"urn:babel:a:b\",\"trace_id\":\"t\",\"data\":{},\"timestamp\":1,"
            + "\"meta\":{\"id\":\"i\",\"queue\":\"q\",\"lang\":\"go\",\"schema_version\":1,\"created_at\":1,"
            + "\"max_retries\":3,\"attempts\":1,\"source\":\"x\",\"ts\":2},\"attempts\":0}";
        var warnings = new List<string>();

        var envelope = EnvelopeCodec.Decode(raw, warnings.Add);

        Assert.True(EnvelopeCodec.Accepts(envelope));
        Assert.Null(envelope.Extras);
        Assert.Null(envelope.Meta!.Extras);
        foreach (var pointer in new[] { "/timestamp", "/meta/max_retries", "/meta/attempts", "/meta/source", "/meta/ts" })
        {
            Assert.Contains(warnings, w => w.Contains($"'{pointer}'", StringComparison.Ordinal));
        }
        Assert.Equal(5, warnings.Count);
    }

    [Fact]
    public void DecodeWithoutHookStillDropsForbiddenKeys()
    {
        var envelope = EnvelopeCodec.Decode(
            "{\"job\":\"urn:babel:a:b\",\"trace_id\":\"t\",\"data\":{},\"timestamp\":1,"
            + "\"meta\":{\"schema_version\":1},\"attempts\":0}");

        Assert.Null(envelope.Extras);
        Assert.DoesNotContain("timestamp", EnvelopeCodec.Encode(envelope), StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeNeverEmitsForbiddenOrCanonicalNamesFromExtras()
    {
        using var forbidden = JsonDocument.Parse("{\"timestamp\":1,\"max_retries\":2,\"job\":\"evil\",\"ok\":true}");
        var extras = new Dictionary<string, JsonElement>();
        foreach (var prop in forbidden.RootElement.EnumerateObject())
        {
            extras[prop.Name] = prop.Value.Clone();
        }
        var made = EnvelopeCodec.Make("urn:babel:a:b");
        var envelope = made with
        {
            Extras = extras,
            Meta = made.Meta! with { Extras = new Dictionary<string, JsonElement>(extras) },
        };

        using var doc = JsonDocument.Parse(EnvelopeCodec.Encode(envelope));
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("timestamp", out _));
        Assert.Equal("urn:babel:a:b", root.GetProperty("job").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        var meta = root.GetProperty("meta");
        Assert.False(meta.TryGetProperty("max_retries", out _));
        Assert.True(meta.GetProperty("ok").GetBoolean());
        // `timestamp` / `job` are only reserved at the top level; inside meta they are plain extras.
        Assert.Equal(1, meta.GetProperty("timestamp").GetInt32());
        Assert.Equal("evil", meta.GetProperty("job").GetString());
    }
}
