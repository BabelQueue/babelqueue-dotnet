namespace BabelQueue;

/// <summary>
/// Jitter modes of the worker-side retry backoff (ADR-0034 section 9). The string values are the
/// canonical identifiers shared with every other SDK.
/// </summary>
public static class RetryJitter
{
    /// <summary>The delay is exactly <c>d(n)</c> (default).</summary>
    public const string None = "none";

    /// <summary>The delay is <c>uniform(0, d(n))</c> (the only jitter mode).</summary>
    public const string Full = "full";
}
