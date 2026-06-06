using System;

namespace BabelQueue;

/// <summary>Base exception for all BabelQueue failures.</summary>
public class BabelQueueException : Exception
{
    public BabelQueueException(string message) : base(message) { }

    public BabelQueueException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Raised when no handler is mapped for a message URN.</summary>
public sealed class UnknownUrnException : BabelQueueException
{
    public UnknownUrnException(string urn)
        : base($"No handler is mapped for the message URN \"{urn}\".") { }
}
