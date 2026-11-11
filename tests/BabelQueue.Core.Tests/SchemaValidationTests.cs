using System.Collections.Generic;
using System.Numerics;
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

    [Fact]
    public void UnionTypeAcceptsAnyListedTypeAndRejectsTheRest()
    {
        var s = Json("{\"type\":[\"string\",\"null\"]}");
        Assert.Null(PayloadValidator.Validate(s, "x"));
        Assert.Null(PayloadValidator.Validate(s, null));
        Assert.NotNull(PayloadValidator.Validate(s, 5L));
        Assert.NotNull(PayloadValidator.Validate(s, false));
        Assert.Equal("<root>: not_in_type_union", PayloadValidator.Validate(s, 5L));
    }

    [Fact]
    public void UnionIntegerAcceptsWholeNumbersInAnyNumericType()
    {
        var s = Json("{\"type\":[\"integer\",\"null\"]}");
        Assert.Null(PayloadValidator.Validate(s, 1.0));
        Assert.Null(PayloadValidator.Validate(s, 100.0));
        Assert.Null(PayloadValidator.Validate(s, 1.0f));
        Assert.Null(PayloadValidator.Validate(s, 1.00m));
        Assert.Null(PayloadValidator.Validate(s, 3));
        Assert.Null(PayloadValidator.Validate(s, BigInteger.Pow(10, 30)));
        Assert.NotNull(PayloadValidator.Validate(s, 1.5));
        Assert.NotNull(PayloadValidator.Validate(s, 1.5m));
        Assert.NotNull(PayloadValidator.Validate(s, "1"));
        Assert.NotNull(PayloadValidator.Validate(s, double.NaN));
    }

    [Fact]
    public void UnionKeepsTheKeywordsOfTheMatchingTypeEnforced()
    {
        var s = Json("{\"type\":[\"integer\",\"null\"],\"minimum\":0}");
        Assert.Null(PayloadValidator.Validate(s, 3L));
        Assert.Null(PayloadValidator.Validate(s, null));
        Assert.Equal("<root>: below_minimum", PayloadValidator.Validate(s, -1L));

        var obj = Json(
            "{\"type\":[\"object\",\"null\"],\"required\":[\"id\"],\"properties\":{\"id\":{\"type\":\"integer\"}}}");
        Assert.Null(PayloadValidator.Validate(obj, Json("{\"id\":1}")));
        Assert.Null(PayloadValidator.Validate(obj, null));
        Assert.Equal("id: missing_required", PayloadValidator.Validate(obj, Json("{}")));
        Assert.Equal("id: not_an_integer", PayloadValidator.Validate(obj, Json("{\"id\":\"x\"}")));

        var str = Json("{\"type\":[\"string\",\"null\"],\"minLength\":2,\"maxLength\":3}");
        Assert.Equal("<root>: below_min_length", PayloadValidator.Validate(str, "a"));
        Assert.Equal("<root>: above_max_length", PayloadValidator.Validate(str, "abcd"));
        Assert.Null(PayloadValidator.Validate(str, "abc"));
    }

    [Fact]
    public void MaxLengthCountsCodePoints()
    {
        var s = Json("{\"type\":\"string\",\"maxLength\":3}");
        Assert.Null(PayloadValidator.Validate(s, "\U0001F600\U0001F600\U0001F600"));
        Assert.NotNull(PayloadValidator.Validate(s, "\U0001F600\U0001F600\U0001F600\U0001F600"));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"string\",\"maxLength\":1}"), "e\u0301"));
    }

    [Fact]
    public void FractionalLengthBoundsAreTruncatedTowardZero()
    {
        // Like Go/PHP/Python/Java: minLength 2.5 behaves as 2, maxLength 2.9 behaves as 2.
        var min = Json("{\"type\":\"string\",\"minLength\":2.5}");
        Assert.Null(PayloadValidator.Validate(min, "ab"));
        Assert.NotNull(PayloadValidator.Validate(min, "a"));

        var max = Json("{\"type\":\"string\",\"maxLength\":2.9}");
        Assert.Null(PayloadValidator.Validate(max, "ab"));
        Assert.NotNull(PayloadValidator.Validate(max, "abc"));

        // A negative fractional bound truncates to 0 (not -0.5): only the empty string fits maxLength.
        var negMax = Json("{\"type\":\"string\",\"maxLength\":-0.5}");
        Assert.Null(PayloadValidator.Validate(negMax, ""));
        Assert.NotNull(PayloadValidator.Validate(negMax, "a"));
        Assert.Null(PayloadValidator.Validate(Json("{\"type\":\"string\",\"minLength\":-0.5}"), ""));
    }

    [Theory]
    [InlineData("minLength")]
    [InlineData("maxLength")]
    public void NonFiniteLengthBoundsAreIgnored(string keyword)
    {
        foreach (var bound in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var schema = new Dictionary<string, object?> { ["type"] = "string", [keyword] = bound };
            foreach (var text in new[] { "", "a", "abc" })
            {
                Assert.Null(PayloadValidator.Validate(schema, text));
            }
        }
    }

    [Fact]
    public void ConstAndEnumCompareNumbersByValueAcrossNumericTypes()
    {
        var one = Json("{\"const\":1}");
        Assert.Null(PayloadValidator.Validate(one, 1L));
        Assert.Null(PayloadValidator.Validate(one, 1.0));
        Assert.Null(PayloadValidator.Validate(one, 1.0f));
        Assert.Null(PayloadValidator.Validate(one, 1));
        Assert.Null(PayloadValidator.Validate(one, 1.000m));
        Assert.Null(PayloadValidator.Validate(one, BigInteger.One));
        Assert.Null(PayloadValidator.Validate(one, 1UL));
        Assert.NotNull(PayloadValidator.Validate(one, 1.5));
        Assert.NotNull(PayloadValidator.Validate(one, true));
        Assert.NotNull(PayloadValidator.Validate(one, "1"));
        Assert.NotNull(PayloadValidator.Validate(one, null));

        // The schema side may carry any numeric type too, and tiny values are not rounded to zero.
        Assert.Null(PayloadValidator.Validate(new Dictionary<string, object?> { ["const"] = 2.50m }, 2.5));
        Assert.Null(PayloadValidator.Validate(new Dictionary<string, object?> { ["const"] = 1 }, 1.0));
        Assert.Null(PayloadValidator.Validate(new Dictionary<string, object?> { ["const"] = 1e-30 }, 1e-30));
        Assert.NotNull(PayloadValidator.Validate(new Dictionary<string, object?> { ["const"] = 0 }, 1e-30));
        Assert.Null(PayloadValidator.Validate(
            new Dictionary<string, object?> { ["enum"] = new List<object?> { 1L, 2.5 } }, 2.50m));
        Assert.Null(PayloadValidator.Validate(
            new Dictionary<string, object?> { ["const"] = 0.0 }, -0.0));

        Assert.NotNull(PayloadValidator.Validate(Json("{\"const\":true}"), 1L));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"const\":false}"), 0L));
        Assert.Null(PayloadValidator.Validate(Json("{\"const\":null}"), null));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"const\":null}"), 0L));
        Assert.Null(PayloadValidator.Validate(Json("{\"enum\":[true,null,0]}"), 0.0));
        Assert.NotNull(PayloadValidator.Validate(Json("{\"enum\":[true,null,0]}"), 1L));
    }

    [Fact]
    public void ConstAndEnumCompareNestedStructuresByValue()
    {
        var c = Json("{\"const\":{\"a\":1,\"b\":[2,{\"c\":3}]}}");
        Assert.Null(PayloadValidator.Validate(c, Json("{\"b\":[2.0,{\"c\":3e0}],\"a\":1.0}")));
        Assert.NotNull(PayloadValidator.Validate(c, Json("{\"a\":1,\"b\":[2,{\"c\":true}]}")));
        Assert.NotNull(PayloadValidator.Validate(c, Json("{\"a\":1,\"b\":[{\"c\":3},2]}")));
        Assert.NotNull(PayloadValidator.Validate(c, Json("{\"a\":1,\"b\":[2,{\"c\":3}],\"d\":1}")));
        var e = Json("{\"enum\":[{\"n\":1},{\"n\":2}]}");
        Assert.Null(PayloadValidator.Validate(e, Json("{\"n\":1.0}")));
        Assert.NotNull(PayloadValidator.Validate(e, Json("{\"n\":\"1\"}")));
    }

    [Fact]
    public void WarningsListEachUnsupportedKeywordWithItsPointer()
    {
        var schema = Json(
            "{\"type\":\"object\",\"minProperties\":2,"
            + "\"properties\":{\"a/b\":{\"type\":\"string\",\"pattern\":\"x\"},"
            + "\"m~n\":{\"type\":\"array\",\"items\":{\"type\":\"integer\",\"multipleOf\":2}}},"
            + "\"additionalProperties\":{\"type\":\"string\",\"format\":\"x\"},"
            + "\"allOf\":[{\"type\":\"integer\",\"pattern\":\"x\"}]}");

        var warnings = PayloadValidator.Warnings(schema);

        Assert.Equal(5, warnings.Count);
        Assert.Contains(new SchemaWarning(string.Empty, "minProperties"), warnings);
        Assert.Contains(new SchemaWarning(string.Empty, "allOf"), warnings);
        Assert.Contains(new SchemaWarning("/properties/a~1b", "pattern"), warnings);
        Assert.Contains(new SchemaWarning("/properties/m~0n/items", "multipleOf"), warnings);
        Assert.Contains(new SchemaWarning("/additionalProperties", "format"), warnings);
        // The unsupported keyword never changes the verdict.
        Assert.Null(PayloadValidator.Validate(Json("{\"type\":\"object\",\"minProperties\":2}"), Json("{\"a\":1}")));
    }

    [Fact]
    public void AnnotationKeywordsAndPropertyNamesNeverWarn()
    {
        var schema = Json(
            "{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"$id\":\"urn:x\",\"title\":\"T\","
            + "\"description\":\"d\",\"$comment\":\"c\",\"examples\":[{}],\"default\":{},\"x-owner\":\"billing\","
            + "\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\",\"x-gdpr-sensitive\":true},"
            + "\"pattern\":{\"type\":\"string\"},\"x-gdpr-sensitive\":{\"type\":\"string\"}}}");
        Assert.Empty(PayloadValidator.Warnings(schema));
    }

    [Fact]
    public void MapProviderWarnsOnRegistrationThroughTheHookAndKeepsTheList()
    {
        var seen = new List<RegisteredSchemaWarning>();

        var p = new MapProvider(
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["urn:babel:a"] = Json("{\"type\":\"string\",\"pattern\":\"^x\"}"),
            },
            seen.Add);

        Assert.Equal(new[] { new RegisteredSchemaWarning("urn:babel:a", new SchemaWarning(string.Empty, "pattern")) }, seen);
        Assert.Equal(seen, p.Warnings);
        Assert.Contains("urn:babel:a", seen[0].Message);
        // Registration warns; validation ignores the keyword.
        Assert.Equal("<root>: not_a_string", SchemaValidation.Check(p, "urn:babel:a", new Dictionary<string, object?>()));
    }

    [Fact]
    public void SameKeywordInTwoSchemasIsAttributedToEachUrn()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"email\":{\"pattern\":\"x\"}}}";

        var p = new MapProvider(
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["urn:babel:a"] = Json(schema),
                ["urn:babel:b"] = Json(schema),
            },
            _ => { });

        Assert.Equal(
            new[]
            {
                new RegisteredSchemaWarning("urn:babel:a", new SchemaWarning("/properties/email", "pattern")),
                new RegisteredSchemaWarning("urn:babel:b", new SchemaWarning("/properties/email", "pattern")),
            },
            p.Warnings);
    }

    [Fact]
    public void MapProviderSkipsNullSchemaValues()
    {
        var p = new MapProvider(
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["urn:babel:none"] = null! },
            _ => { });

        Assert.Empty(p.Warnings);
        Assert.Null(p.SchemaFor("urn:babel:none"));
    }

    [Fact]
    public void MapProviderFromJsonRoutesWarningsToTheHook()
    {
        var seen = new List<RegisteredSchemaWarning>();

        var p = MapProvider.FromJson(
            new Dictionary<string, string> { ["urn:babel:a"] = "{\"type\":\"object\",\"minProperties\":1}" },
            seen.Add);

        Assert.Equal(new[] { new RegisteredSchemaWarning("urn:babel:a", new SchemaWarning(string.Empty, "minProperties")) }, p.Warnings);
        Assert.Equal(p.Warnings, seen);
    }

    [Fact]
    public void NonIntegerLengthLimitsAreEnforced()
    {
        // minLength/maxLength given as a double (2.0) used to be ignored (`ml is long`).
        Assert.NotNull(PayloadValidator.Validate(Json("{\"type\":\"string\",\"minLength\":2.0}"), "a"));
        Assert.Null(PayloadValidator.Validate(Json("{\"type\":\"string\",\"minLength\":2.0}"), "ab"));
    }
}
