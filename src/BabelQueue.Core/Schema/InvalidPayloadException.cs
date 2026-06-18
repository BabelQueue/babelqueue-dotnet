using BabelQueue;

namespace BabelQueue.Schema;

/// <summary>
/// Raised when a message's <c>data</c> does not match the JSON Schema registered for its URN
/// (ADR-0024). The consumer-side <see cref="SchemaValidation.Wrap"/> throws it so the adapter
/// redelivers (and eventually dead-letters) a poison message; the recommended primary use is
/// producer-side (<see cref="SchemaValidation.Validate"/>) so invalid data never enters the queue.
/// </summary>
public sealed class InvalidPayloadException : BabelQueueException
{
    /// <summary>Create the exception for a URN whose data violated its schema.</summary>
    public InvalidPayloadException(string urn, string violation)
        : base($"Message data for \"{urn}\" does not match its URN schema: {violation}.")
    {
        Urn = urn;
        Violation = violation;
    }

    /// <summary>The message URN whose schema was violated.</summary>
    public string Urn { get; }

    /// <summary>The first <c>"&lt;json-pointer&gt;: &lt;reason&gt;"</c> mismatch.</summary>
    public string Violation { get; }
}
