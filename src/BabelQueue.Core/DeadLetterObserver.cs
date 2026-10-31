using System;
using System.Diagnostics;

namespace BabelQueue;

/// <summary>
/// What a consumer reports to an <see cref="IDeadLetterObserver"/> after it carried out a dead-letter
/// (ADR-0039 section 5.1).
/// </summary>
/// <param name="Queue">
/// The source queue/topic the message was consumed from (<c>meta.queue</c>, else the consumer's
/// configured queue); empty when unknown.
/// </param>
/// <param name="DeadLetterQueue">
/// The DLQ the record went to; empty for a native dead-letter (the broker's own
/// <c>$DeadLetterQueue</c> sub-entity).
/// </param>
/// <param name="Reason">The <c>dead_letter.reason</c> the SDK wrote; empty for a raw body.</param>
public sealed record DeadLetterEvent(string Queue = "", string DeadLetterQueue = "", string Reason = "");

/// <summary>
/// An optional hook a consuming component calls once per dead-letter the SDK <b>itself</b> carried out,
/// <b>after</b> it is confirmed (ADR-0039 section 5.1): a record written to the BabelQueue DLQ, or an
/// SDK-initiated native dead-letter that the broker confirmed. It is not called when the write or the
/// native call failed, when the broker dead-letters on its own, or when DLQ writing is disabled.
/// </summary>
/// <remarks>
/// <para>
/// The .NET core has no consume runtime and no DLQ write point of its own (ADR-0039 section 5.2): the
/// transport packages call the observer from their DLQ write primitives. The core owns only this seam
/// so every transport takes the same type. <see cref="Tracing.MetricsDeadLetterObserver"/> is the
/// implementation that increments <c>babelqueue.dlq.messages</c>.
/// </para>
/// <para>
/// Isolation: a transport MUST call the observer through
/// <see cref="DeadLetterObservers.NotifySafely"/>, which catches and logs any exception, so an
/// observer can never change the settle outcome (no ack, nack or retry is skipped or added).
/// </para>
/// </remarks>
public interface IDeadLetterObserver
{
    /// <summary>Called once per completed dead-letter. Keep it fast and non-blocking.</summary>
    /// <param name="deadLetterEvent">What was dead-lettered and where it went.</param>
    void OnDeadLetter(DeadLetterEvent deadLetterEvent);
}

/// <summary>Helpers for transports that call an <see cref="IDeadLetterObserver"/>.</summary>
public static class DeadLetterObservers
{
    /// <summary>
    /// Calls <paramref name="observer"/> (a no-op when it is <c>null</c>) and swallows any exception it
    /// raises, writing a warning to <see cref="Trace.TraceWarning(string)"/> (ADR-0039 section 5.1,
    /// Isolation). The exception type is logged; the message is not, since it may carry payload data.
    /// </summary>
    /// <param name="observer">The registered observer, or <c>null</c>.</param>
    /// <param name="deadLetterEvent">The event to report.</param>
    public static void NotifySafely(this IDeadLetterObserver? observer, DeadLetterEvent deadLetterEvent)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            observer.OnDeadLetter(deadLetterEvent);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "BabelQueue: the dead-letter observer threw " + ex.GetType().FullName + "; ignored, the settle outcome is unchanged.");
        }
    }
}
