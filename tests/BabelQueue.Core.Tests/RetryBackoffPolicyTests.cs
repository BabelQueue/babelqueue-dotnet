using System;
using BabelQueue;
using Xunit;

namespace BabelQueue.Tests;

public class RetryBackoffPolicyTests
{
    [Fact]
    public void DefaultsYieldZeroDelay()
    {
        Assert.Equal(0, RetryBackoffPolicy.Default.DelaySeconds(1));
        Assert.Equal(0, RetryBackoffPolicy.Default.DelaySeconds(50));
    }

    [Fact]
    public void ConstantBaseKeepsTheConstantDelay()
    {
        var policy = new RetryBackoffPolicy(retryBackoff: 5);
        Assert.Equal(5, policy.DelaySeconds(1));
        Assert.Equal(5, policy.DelaySeconds(9));
    }

    [Fact]
    public void ExponentialGrowthIsCapped()
    {
        var policy = new RetryBackoffPolicy(1, 2, 10);
        Assert.Equal(1, policy.DelaySeconds(1));
        Assert.Equal(8, policy.DelaySeconds(4));
        Assert.Equal(10, policy.DelaySeconds(5));
        Assert.Equal(10, policy.DelaySeconds(2000));
    }

    [Fact]
    public void FullJitterIsUniformUpToTheDeterministicDelay()
    {
        Assert.Equal(0, new RetryBackoffPolicy(8, 1, 0, RetryJitter.Full, () => 0.0).DelaySeconds(1));
        Assert.Equal(4, new RetryBackoffPolicy(8, 1, 0, RetryJitter.Full, () => 0.5).DelaySeconds(1));
        Assert.Equal(8, new RetryBackoffPolicy(8, 1, 0, RetryJitter.Full, () => 1.0).DelaySeconds(1));
    }

    [Fact]
    public void OverflowSaturatesInsteadOfProducingInfinity()
    {
        var policy = new RetryBackoffPolicy(1, 10);
        Assert.True(double.IsFinite(policy.DelaySeconds(5000)));
    }

    [Theory]
    [InlineData(-1, 1, 0)]
    [InlineData(double.NaN, 1, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 1, -1)]
    [InlineData(double.PositiveInfinity, 1, 0)]
    public void InvalidNumbersAreRejected(double backoff, double multiplier, double max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryBackoffPolicy(backoff, multiplier, max));
    }

    [Fact]
    public void UnknownJitterIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new RetryBackoffPolicy(retryJitter: "equal"));
    }

    [Fact]
    public void AttemptNumberIsOneBased()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryBackoffPolicy.Default.DelaySeconds(0));
    }
}
