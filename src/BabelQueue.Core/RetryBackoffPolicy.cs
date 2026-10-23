namespace BabelQueue;

/// <summary>
/// The worker-side retry backoff of ADR-0034 section 9, shared by every transport that delays a
/// failed message itself (SQS, Redis, AMQP). Kafka does not apply it: there the retry tier sets the delay.
/// <para>
/// <c>d(n) = min(RetryBackoffMax, RetryBackoff * RetryBackoffMultiplier^(n-1))</c> seconds, where
/// <c>n</c> is the 1-based attempt number (<c>attempts + 1</c>) and <c>RetryBackoffMax = 0</c>
/// means "no cap" (a transport limit, such as the SQS 12 h visibility ceiling, still applies on top).
/// With <see cref="RetryJitter.Full"/> the delay is <c>uniform(0, d(n))</c>.
/// </para>
/// <para>
/// The defaults (<c>0</c>, <c>1</c>, <c>0</c>, <see cref="RetryJitter.None"/>) yield a delay of
/// <c>0</c>, and a constant <see cref="RetryBackoff"/> with multiplier <c>1</c> keeps the pre-existing
/// constant-delay behaviour.
/// </para>
/// </summary>
public sealed class RetryBackoffPolicy
{
    private readonly Func<double> _random;

    /// <summary>The policy with every default: no delay.</summary>
    public static RetryBackoffPolicy Default { get; } = new();

    /// <param name="retryBackoff">Base delay in seconds (&gt;= 0).</param>
    /// <param name="retryBackoffMultiplier">Growth factor per attempt (&gt; 0).</param>
    /// <param name="retryBackoffMax">Cap in seconds (&gt;= 0); <c>0</c> = no cap.</param>
    /// <param name="retryJitter"><see cref="RetryJitter.None"/> or <see cref="RetryJitter.Full"/>.</param>
    /// <param name="random">Source of uniform draws in <c>[0, 1)</c>; defaults to <see cref="Random.Shared"/>. Tests inject a fixed one.</param>
    public RetryBackoffPolicy(
        double retryBackoff = 0,
        double retryBackoffMultiplier = 1,
        double retryBackoffMax = 0,
        string retryJitter = RetryJitter.None,
        Func<double>? random = null)
    {
        if (double.IsNaN(retryBackoff) || double.IsInfinity(retryBackoff) || retryBackoff < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryBackoff), retryBackoff, "retry_backoff must be a finite number >= 0.");
        }
        if (double.IsNaN(retryBackoffMultiplier) || double.IsInfinity(retryBackoffMultiplier) || retryBackoffMultiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryBackoffMultiplier), retryBackoffMultiplier, "retry_backoff_multiplier must be a finite number > 0.");
        }
        if (double.IsNaN(retryBackoffMax) || double.IsInfinity(retryBackoffMax) || retryBackoffMax < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryBackoffMax), retryBackoffMax, "retry_backoff_max must be a finite number >= 0 (0 = no cap).");
        }
        if (retryJitter != RetryJitter.None && retryJitter != RetryJitter.Full)
        {
            throw new ArgumentException($"retry_jitter must be '{RetryJitter.None}' or '{RetryJitter.Full}', got '{retryJitter}'.", nameof(retryJitter));
        }

        RetryBackoff = retryBackoff;
        RetryBackoffMultiplier = retryBackoffMultiplier;
        RetryBackoffMax = retryBackoffMax;
        Jitter = retryJitter;
        _random = random ?? (() => Random.Shared.NextDouble());
    }

    /// <summary>Base delay in seconds.</summary>
    public double RetryBackoff { get; }

    /// <summary>Growth factor per attempt.</summary>
    public double RetryBackoffMultiplier { get; }

    /// <summary>Cap in seconds; <c>0</c> = no cap.</summary>
    public double RetryBackoffMax { get; }

    /// <summary>The jitter mode.</summary>
    public string Jitter { get; }

    /// <summary>
    /// The deterministic delay <c>d(n)</c> in seconds, before any jitter.
    /// </summary>
    /// <param name="n">1-based attempt number (<c>attempts + 1</c>).</param>
    public double MaxDelaySeconds(int n)
    {
        if (n < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, "n is the 1-based attempt number (attempts + 1).");
        }
        if (RetryBackoff == 0)
        {
            return 0;
        }

        var d = RetryBackoff * Math.Pow(RetryBackoffMultiplier, n - 1);
        if (double.IsInfinity(d) || double.IsNaN(d))
        {
            d = double.MaxValue;
        }
        return RetryBackoffMax > 0 ? Math.Min(RetryBackoffMax, d) : d;
    }

    /// <summary>
    /// The delay, in seconds, to apply before attempt <paramref name="n"/>: <c>d(n)</c> with
    /// <see cref="RetryJitter.None"/>, <c>uniform(0, d(n))</c> with <see cref="RetryJitter.Full"/>.
    /// </summary>
    /// <param name="n">1-based attempt number (<c>attempts + 1</c>).</param>
    public double DelaySeconds(int n)
    {
        var d = MaxDelaySeconds(n);
        return Jitter == RetryJitter.Full ? _random() * d : d;
    }
}
