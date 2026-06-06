using System.Collections.Generic;
using BabelQueue;
using Xunit;

namespace BabelQueue.Tests;

public class DeadLettersTests
{
    [Fact]
    public void AnnotateAttachesBlockWithoutMutatingTheOriginal()
    {
        var env = EnvelopeCodec.Make(
            "urn:babel:orders:created",
            new Dictionary<string, object?> { ["order_id"] = 1L },
            queue: "orders");

        var dl = DeadLetters.Annotate(env, "failed", "orders", attempts: 3, error: "boom", exception: "System.Exception");

        Assert.Null(env.DeadLetter);
        Assert.NotNull(dl.DeadLetter);
        Assert.Equal("failed", dl.DeadLetter!.Reason);
        Assert.Equal("orders", dl.DeadLetter.OriginalQueue);
        Assert.Equal(3, dl.DeadLetter.Attempts);
        Assert.Equal("boom", dl.DeadLetter.Error);
        Assert.Equal("System.Exception", dl.DeadLetter.Exception);
        Assert.Equal(EnvelopeCodec.SourceLang, dl.DeadLetter.Lang);
        Assert.True(dl.DeadLetter.FailedAt > 0);
    }

    [Fact]
    public void AnnotateDefaultsErrorToNullAndAttemptsToEnvelope()
    {
        var env = EnvelopeCodec.Make("urn:babel:orders:created", new Dictionary<string, object?> { ["order_id"] = 1L });
        var dl = DeadLetters.Annotate(env, "expired", "orders");

        Assert.Null(dl.DeadLetter!.Error);
        Assert.Null(dl.DeadLetter.Exception);
        Assert.Equal(0, dl.DeadLetter.Attempts);
    }

    [Fact]
    public void DeadLetterIsEncodedAsTheLastTopLevelField()
    {
        var env = EnvelopeCodec.Make("urn:babel:orders:created", new Dictionary<string, object?> { ["order_id"] = 1L });
        var body = EnvelopeCodec.Encode(DeadLetters.Annotate(env, "failed", "orders"));

        Assert.Contains("\"dead_letter\":", body);
        Assert.True(body.IndexOf("\"dead_letter\"", System.StringComparison.Ordinal)
                    > body.IndexOf("\"attempts\"", System.StringComparison.Ordinal));
    }
}
