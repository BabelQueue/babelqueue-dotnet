namespace BabelQueue;

/// <summary>
/// What a consumer does with a message whose URN has no registered handler. The
/// string values are the canonical wire identifiers, shared with every other SDK.
/// </summary>
public static class UnknownUrnStrategy
{
    /// <summary>Surface an error; let the worker decide.</summary>
    public const string Fail = "fail";

    /// <summary>Drop the message.</summary>
    public const string Delete = "delete";

    /// <summary>Requeue for another consumer.</summary>
    public const string Release = "release";

    /// <summary>Route to the dead-letter queue.</summary>
    public const string DeadLetter = "dead_letter";
}
