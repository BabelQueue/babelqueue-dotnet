using System.Text;
using Xunit;

namespace BabelQueue.Tests;

public class BodyVerdictTests
{
    [Fact]
    public void WellFormedEnvelopeIsOk()
    {
        var body = EnvelopeCodec.Encode(EnvelopeCodec.Make("urn:babel:orders:created"));

        var verdict = BodyVerdict.Classify(body);

        Assert.True(verdict.IsOk);
        Assert.False(verdict.IsPoison);
        Assert.Equal(BodyVerdictKind.Ok, verdict.Kind);
        Assert.Null(verdict.Cause);
    }

    [Theory]
    [InlineData("{\"job\":\"urn:x\",\"attempts\":\"3\"}", "attempts")]
    [InlineData("{\"job\":\"urn:x\",\"data\":[]}", "data")]
    [InlineData("{\"job\":\"urn:x\",\"meta\":{\"id\":5}}", "meta.id")]
    [InlineData("{\"job\":\"urn:x\",\"trace_id\":7}", "trace_id")]
    [InlineData("{\"job\":\"urn:x\",\"dead_letter\":{\"failed_at\":1.5}}", "dead_letter.failed_at")]
    public void WrongTypedKnownMemberIsPoisonWithTheMemberInTheCause(string body, string member)
    {
        var verdict = BodyVerdict.Classify(body);

        Assert.True(verdict.IsPoison);
        Assert.Contains(member, verdict.Cause);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"job\":5}")]
    [InlineData("{\"job\":\"  \"}")]
    [InlineData("{\"job\":\"urn:x\"} trailing")]
    [InlineData("{\"job\":\"urn:x\",\"job\":5}")]
    public void UndecodableOrIdentitylessBodyIsPoison(string? body)
    {
        var verdict = BodyVerdict.Classify(body);

        Assert.True(verdict.IsPoison);
        Assert.False(string.IsNullOrEmpty(verdict.Cause));
    }

    [Theory]
    [InlineData("{\"urn\":\"urn:x\"}")]
    [InlineData("{\"job\":\"  \",\"urn\":\"urn:x\"}")]
    [InlineData("{\"job\":5,\"urn\":\"urn:x\"}")]
    [InlineData("{\"job\":5,\"job\":\"urn:x\"}")]
    [InlineData("{\"job\":\"urn:x\",\"data\":null,\"meta\":null,\"attempts\":null,\"dead_letter\":null}")]
    public void ValidIdentityIsOk(string body)
    {
        Assert.True(BodyVerdict.Classify(body).IsOk);
    }

    [Fact]
    public void BomPrefixedBodyIsPoison()
    {
        Assert.True(BodyVerdict.Classify("\uFEFF{\"job\":\"urn:x\"}").IsPoison);
    }

    [Fact]
    public void BodyNestedDeeperThanTheCoreCodecIsPoison()
    {
        var depth = BodyVerdict.CoreMaxDepth + 1;
        var body = new StringBuilder("{\"job\":\"urn:x\",\"x\":");
        for (var i = 0; i < depth; i++)
        {
            body.Append('[');
        }
        for (var i = 0; i < depth; i++)
        {
            body.Append(']');
        }
        body.Append('}');

        Assert.True(BodyVerdict.Classify(body.ToString()).IsPoison);
    }

    // System.Text.Json cannot materialise a lone UTF-16 surrogate escape, so EnvelopeCodec.Decode throws on it
    // wherever it sits. Go, Python and Node accept such a body; here it is poison (a documented platform limit), so
    // a consumer that decodes every Ok body never meets an uncaught exception.
    [Theory]
    [InlineData("{\"job\":\"urn:x\",\"\\ud800\":1}")]
    [InlineData("{\"job\":\"\\ud800\"}")]
    [InlineData("{\"job\":\"urn:x\",\"urn\":\"\\ud800\"}")]
    [InlineData("{\"job\":\"urn:x\",\"trace_id\":\"\\ud800\"}")]
    [InlineData("{\"job\":\"urn:x\",\"data\":{\"a\":\"\\ud800\"}}")]
    [InlineData("{\"job\":\"urn:x\",\"data\":{\"\\udc00\":1}}")]
    [InlineData("{\"job\":\"urn:x\",\"meta\":{\"id\":\"\\ud800\"}}")]
    [InlineData("{\"job\":\"urn:x\",\"meta\":{\"\\ud800\":1}}")]
    [InlineData("{\"job\":\"urn:x\",\"x\":[\"\\ud800\"]}")]
    public void LoneSurrogateEscapeAnywhereIsPoisonNotAThrow(string body)
    {
        Assert.True(BodyVerdict.Classify(body).IsPoison);
    }

    [Theory]
    [InlineData("{\"job\":\"urn:x\",\"data\":{\"a\":\"\\ud83d\\ude00\"}}")]
    [InlineData("{\"job\":\"urn:x\",\"trace_id\":\"t\u00e9\"}")]
    public void ValidSurrogatePairIsOk(string body)
    {
        Assert.True(BodyVerdict.Classify(body).IsOk);
        Assert.Equal("urn:x", EnvelopeCodec.Urn(EnvelopeCodec.Decode(body, static _ => { })));
    }

    [Theory]
    [InlineData("{\"job\":\"urn:x\"}")]
    [InlineData("{\"urn\":\"urn:x\",\"meta\":null,\"data\":null,\"attempts\":null}")]
    [InlineData("{\"job\":\"urn:x\",\"data\":{\"a\":[1,\"two\",{\"b\":null}]},\"meta\":{\"id\":\"i\",\"x\":\"y\"}}")]
    [InlineData("{\"job\":\"urn:x\",\"attempts\":2,\"dead_letter\":{\"reason\":\"r\",\"failed_at\":1}}")]
    public void AnOkBodyIsOneDecodeReturnsFor(string body)
    {
        Assert.True(BodyVerdict.Classify(body).IsOk);
        var envelope = EnvelopeCodec.Decode(body, static _ => { });
        Assert.Equal("urn:x", EnvelopeCodec.Urn(envelope));
    }

    [Theory]
    [InlineData("{\"job\":\"urn:x\",\"trace_id\":\"\\ud800\"}")]
    [InlineData("{\"job\":\"urn:x\",\"data\":{\"a\":\"\\ud800\"}}")]
    [InlineData("{\"job\":\"urn:x\",\"meta\":{\"id\":\"\\ud800\"}}")]
    public void EveryBodyDecodeThrowsOnIsPoison(string body)
    {
        Assert.ThrowsAny<InvalidOperationException>(() => EnvelopeCodec.Decode(body, static _ => { }));
        Assert.True(BodyVerdict.Classify(body).IsPoison);
    }

    [Fact]
    public void DefaultResultIsPoisonNeverOk()
    {
        BodyVerdictResult unclassified = default;

        Assert.False(unclassified.IsOk);
        Assert.True(unclassified.IsPoison);
        Assert.Equal(BodyVerdictKind.Poison, default(BodyVerdictKind));
        Assert.Equal(BodyVerdictKind.Poison, unclassified.Kind);
    }

    [Fact]
    public void DecodeStaysLenientForAWronglyTypedKnownMember()
    {
        // The additive verdict API must not change EnvelopeCodec.Decode: it still maps a wrong type to a default.
        const string body = "{\"job\":\"urn:x\",\"attempts\":\"3\"}";

        var envelope = EnvelopeCodec.Decode(body, static _ => { });

        Assert.Equal("urn:x", EnvelopeCodec.Urn(envelope));
        Assert.Equal(0, envelope.Attempts);
        Assert.True(BodyVerdict.Classify(body).IsPoison);
    }

    [Fact]
    public void DecodeStaysLenientForAWronglyTypedData()
    {
        const string body = "{\"job\":\"urn:x\",\"data\":[]}";

        var envelope = EnvelopeCodec.Decode(body, static _ => { });

        Assert.Equal("urn:x", EnvelopeCodec.Urn(envelope));
        Assert.Null(envelope.Data);
        Assert.True(BodyVerdict.Classify(body).IsPoison);
    }
}
