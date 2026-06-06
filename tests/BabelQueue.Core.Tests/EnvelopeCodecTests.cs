using System.Collections.Generic;
using BabelQueue;
using Xunit;

namespace BabelQueue.Tests;

public class EnvelopeCodecTests
{
    [Fact]
    public void MakeProducesCanonicalShape()
    {
        var env = EnvelopeCodec.Make("urn:babel:orders:created", new Dictionary<string, object?> { ["order_id"] = 1042L });

        Assert.Equal("urn:babel:orders:created", env.Job);
        Assert.Equal(0, env.Attempts);
        Assert.Equal(EnvelopeCodec.SourceLang, env.Meta!.Lang);
        Assert.Equal(EnvelopeCodec.SchemaVersion, env.Meta.SchemaVersion);
        Assert.Equal("default", env.Meta.Queue);
        Assert.False(string.IsNullOrWhiteSpace(env.TraceId));
        Assert.False(string.IsNullOrWhiteSpace(env.Meta.Id));
        Assert.NotEqual(env.TraceId, env.Meta.Id);
        Assert.True(env.Meta.CreatedAt > 0);
        Assert.Null(env.DeadLetter);
    }

    [Fact]
    public void MakeRejectsBlankUrn()
    {
        Assert.Throws<BabelQueueException>(() => EnvelopeCodec.Make("   ", new Dictionary<string, object?>()));
    }

    [Fact]
    public void MakeHonorsQueueAndTraceContinuation()
    {
        var env = EnvelopeCodec.Make(
            "urn:babel:orders:created",
            new Dictionary<string, object?> { ["order_id"] = 1L },
            queue: "orders",
            traceId: "trace-123");

        Assert.Equal("orders", env.Meta!.Queue);
        Assert.Equal("trace-123", env.TraceId);
    }

    [Fact]
    public void EncodeDecodeRoundTrips()
    {
        var env = EnvelopeCodec.Make(
            "urn:babel:orders:created",
            new Dictionary<string, object?> { ["order_id"] = 1042L },
            queue: "orders");
        var got = EnvelopeCodec.Decode(EnvelopeCodec.Encode(env));

        Assert.True(EnvelopeCodec.Accepts(got));
        Assert.Equal(env.Job, EnvelopeCodec.Urn(got));
        Assert.Equal(env.TraceId, got.TraceId);
        Assert.Equal(env.Meta!.Id, got.Meta!.Id);
        Assert.Equal(1042L, got.Data!["order_id"]);
    }

    [Fact]
    public void EncodeIsCanonicalAndUnescaped()
    {
        var data = new Dictionary<string, object?>
        {
            ["title"] = "Café — naïve ☕ A & B <x>/y",
            ["qty"] = 7L,
            ["ratio"] = 0.5,
            ["active"] = true,
            ["note"] = null,
        };
        var env = new Envelope(
            "urn:babel:catalog:item.indexed",
            "t-123",
            data,
            new Meta("m-1", "catalog", "dotnet", 1, 1749132727000L),
            2,
            null);

        const string expected =
            "{\"job\":\"urn:babel:catalog:item.indexed\",\"trace_id\":\"t-123\","
            + "\"data\":{\"title\":\"Café — naïve ☕ A & B <x>/y\",\"qty\":7,\"ratio\":0.5,"
            + "\"active\":true,\"note\":null},"
            + "\"meta\":{\"id\":\"m-1\",\"queue\":\"catalog\",\"lang\":\"dotnet\","
            + "\"schema_version\":1,\"created_at\":1749132727000},\"attempts\":2}";

        Assert.Equal(expected, EnvelopeCodec.Encode(env));
    }

    [Fact]
    public void DecodeResolvesUrnAlias()
    {
        const string raw =
            "{\"urn\":\"urn:babel:orders:created\",\"trace_id\":\"t\",\"data\":{},"
            + "\"meta\":{\"id\":\"i\",\"queue\":\"q\",\"lang\":\"dotnet\","
            + "\"schema_version\":1,\"created_at\":1},\"attempts\":0}";
        var env = EnvelopeCodec.Decode(raw);

        Assert.Equal("urn:babel:orders:created", EnvelopeCodec.Urn(env));
        Assert.True(EnvelopeCodec.Accepts(env));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    public void DecodeReturnsEmptyForNonObjectInput(string raw)
    {
        Assert.False(EnvelopeCodec.Accepts(EnvelopeCodec.Decode(raw)));
    }

    [Fact]
    public void AcceptsRejectsMalformedEnvelopes()
    {
        var ok = EnvelopeCodec.Make("urn:babel:orders:created", new Dictionary<string, object?> { ["x"] = 1L });
        Assert.True(EnvelopeCodec.Accepts(ok));

        Assert.False(EnvelopeCodec.Accepts(ok with { Job = null }));
        Assert.False(EnvelopeCodec.Accepts(ok with { Meta = ok.Meta! with { SchemaVersion = 2 } }));
        Assert.False(EnvelopeCodec.Accepts(ok with { TraceId = "  " }));
        Assert.False(EnvelopeCodec.Accepts(ok with { Data = null }));
    }

    [Fact]
    public void FromMessageBuildsAndContinuesTrace()
    {
        var env = EnvelopeCodec.FromMessage(new OrderCreated(7L), "orders");

        Assert.Equal("urn:babel:orders:created", env.Job);
        Assert.Equal(7L, env.Data!["order_id"]);
        Assert.Equal("carry-over", env.TraceId);
        Assert.Equal("orders", env.Meta!.Queue);
    }

    private sealed class OrderCreated : IPolyglotMessage, IHasTraceId
    {
        private readonly long _orderId;

        public OrderCreated(long orderId) => _orderId = orderId;

        public string GetBabelUrn() => "urn:babel:orders:created";

        public IReadOnlyDictionary<string, object?> ToPayload() =>
            new Dictionary<string, object?> { ["order_id"] = _orderId };

        public string? GetBabelTraceId() => "carry-over";
    }
}
