namespace BabelQueue;

/// <summary>
/// The additive block appended to an <see cref="Envelope"/> when a message is
/// dead-lettered. The original envelope is preserved unchanged alongside it, so a
/// consumer in any language can still read the original job, data and trace id.
/// </summary>
/// <param name="Reason">Why the message was dead-lettered.</param>
/// <param name="Error">A human-readable error message, or <c>null</c>.</param>
/// <param name="Exception">The originating exception type/class name, or <c>null</c>.</param>
/// <param name="FailedAt">Failure time in Unix milliseconds, UTC.</param>
/// <param name="OriginalQueue">The queue the message was consumed from.</param>
/// <param name="Attempts">How many delivery attempts were made.</param>
/// <param name="Lang">The SDK language that dead-lettered the message.</param>
public sealed record DeadLetter(
    string Reason,
    string? Error,
    string? Exception,
    long FailedAt,
    string OriginalQueue,
    int Attempts,
    string Lang);
