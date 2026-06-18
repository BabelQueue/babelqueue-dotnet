using System.Collections.Generic;
using System.Threading.Tasks;
using BabelQueue;
using BabelQueue.Schema;
using Xunit;

namespace BabelQueue.Tests;

public class SchemaValidationTests
{
    private const string Orders =
        "{\"type\":\"object\",\"required\":[\"order_id\"]," +
        "\"properties\":{\"order_id\":{\"type\":\"integer\"}},\"additionalProperties\":false}";

    private static IReadOnlyDictionary<string, object?> Json(string raw) => SchemaJson.ParseObject(raw);

    private static ISchemaProvider Provider() =>
        new MapProvider(new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["urn:babel:orders:created"] = Json(Orders),
        });

    [Fact]
    public void ValidatorEnforcesObjectRequiredTypesAndAdditional()
    {
        var s = Json(Orders);
        Assert.Null(PayloadValidator.Validate(s, Json("{\"order_id\":7}")));
        Assert.NotNull(PayloadValidator.Validate(s, Json("{}")));
        Assert.NotNull(PayloadValidator.Validate(s, Json("{\"order_id\":\"x\"}")));
        Assert.NotNull(PayloadValidator.Validate(s, Json("{\"order_id\":7,\"extra\":1}")));
    }

    [Fact]
    public void ValidatorScalarParity()
    {
        Assert.Null(PayloadValidator.Validate(Json("{\"type\":\"boolean\"}"), true));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"boolean\"}"), "x"));
        Assert.Null(PayloadValidator.Validate(Json("{\"type\":\"null\"}"), null));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"null\"}"), 1L));
        Assert.Null(PayloadValidator.Validate(Json("{\"type\":\"number\",\"minimum\":0.5}"), 0.6));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"number\",\"minimum\":0.5}"), 0.4));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"number\"}"), "x"));
        Assert.Null(PayloadValidator.Validate(Json("{\"type\":\"integer\"}"), 1L));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"integer\"}"), 1.5));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"integer\"}"), true));
    }

    [Fact]
    public void ValidatorConstStringEnumArray()
    {
        Assert.Null(PayloadValidator.Validate(Json("{\"const\":\"v1\"}"), "v1"));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"const\":\"v1\"}"), "v2"));
        Assert.Null(PayloadValidator.Validate(Json("{\"type\":\"string\",\"minLength\":1}"), "a"));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"string\",\"minLength\":1}"), ""));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"string\"}"), 5L));
        Assert.Null(PayloadValidator.Validate(Json("{\"enum\":[\"a\",\"b\"]}"), "b"));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"enum\":[\"a\",\"b\"]}"), "c"));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"object\"}"), "x"));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"array\"}"), "x"));
        var arr = Json("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}");
        Assert.Null(PayloadValidator.Validate(arr, new List<object?> { "a", "b" }));
        Assert.NotNull(PayloadValidator.Validate(arr, new List<object?> { "a", 1L }));
    }

    [Fact]
    public void CheckValidInvalidUnregistered()
    {
        var p = Provider();
        Assert.Null(SchemaValidation.Check(p, "urn:babel:orders:created", Json("{\"order_id\":1}")));
        Assert.Null(SchemaValidation.Check(p, "urn:babel:unknown", Json("{\"x\":1}")));
        Assert.NotNull(SchemaValidation.Check(p, "urn:babel:orders:created", Json("{}")));
    }

    [Fact]
    public void ValidateThrowsWithDetails()
    {
        var ex = Assert.Throws<InvalidPayloadException>(() =>
            SchemaValidation.Validate(Provider(), "urn:babel:orders:created", Json("{\"order_id\":\"x\"}")));
        Assert.Equal("urn:babel:orders:created", ex.Urn);
        Assert.NotNull(ex.Violation);
    }

    [Fact]
    public async Task WrapRunsThrowsAndRunsForUnregistered()
    {
        var p = Provider();
        var calls = 0;
        Handler handler = SchemaValidation.Wrap(p, _ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await handler(EnvelopeCodec.Make("urn:babel:orders:created", Json("{\"order_id\":1}")));
        Assert.Equal(1, calls);

        await Assert.ThrowsAsync<InvalidPayloadException>(() =>
            handler(EnvelopeCodec.Make("urn:babel:orders:created", Json("{}"))));
        Assert.Equal(1, calls);

        await handler(EnvelopeCodec.Make("urn:babel:unknown", Json("{\"anything\":true}")));
        Assert.Equal(2, calls);
    }
}
