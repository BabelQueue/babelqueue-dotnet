using System;

namespace BabelQueue;

/// <summary>Builds the additive <c>dead_letter</c> block for an <see cref="Envelope"/>.</summary>
public static class DeadLetters
{
    /// <summary>
    /// Returns a copy of the envelope with a <see cref="DeadLetter"/> block attached,
    /// recording why and where it failed. The original envelope is preserved unchanged
    /// (records are immutable), so any-language consumers can still read it.
    /// </summary>
    /// <param name="envelope">The envelope to dead-letter.</param>
    /// <param name="reason">Why the message is being dead-lettered.</param>
    /// <param name="originalQueue">The queue the message was consumed from.</param>
    /// <param name="attempts">Delivery attempts made; defaults to the envelope's current count.</param>
    /// <param name="error">A human-readable error message, or <c>null</c>.</param>
    /// <param name="exception">The originating exception type/name, or <c>null</c>.</param>
    public static Envelope Annotate(
        Envelope envelope,
        string reason,
        string originalQueue,
        int? attempts = null,
        string? error = null,
        string? exception = null)
    {
        var deadLetter = new DeadLetter(
            reason,
            error,
            exception,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            originalQueue,
            attempts ?? envelope.Attempts,
            EnvelopeCodec.SourceLang);

        return envelope with { DeadLetter = deadLetter };
    }
}
