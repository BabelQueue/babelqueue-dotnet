using System;
using BabelQueue;

namespace BabelQueue.Gdpr;

/// <summary>
/// Raised by <see cref="Gdpr.Unprotect"/> when a protected field cannot be restored — a wrong key,
/// a tampered / garbled ciphertext, or a value that is not the string <see cref="Gdpr.Protect"/>
/// produced (ADR-0030). It is the .NET mirror of the Go <c>gdpr.ErrDecrypt</c>: distinguishable
/// from a missing field (which is skipped, not an error), so the consumer fails the message and
/// it takes the retry / dead-letter path rather than processing unreadable PII.
/// </summary>
public sealed class ProtectedFieldException : BabelQueueException
{
    /// <summary>Create the exception for a sensitive field that could not be decrypted.</summary>
    /// <param name="path">The dotted path of the field that failed to decrypt.</param>
    /// <param name="innerException">The underlying crypto/JSON failure.</param>
    public ProtectedFieldException(string path, Exception innerException)
        : base($"Cannot decrypt the protected field \"{path}\".", innerException)
    {
        Path = path;
    }

    /// <summary>The dotted path of the field that could not be decrypted.</summary>
    public string Path { get; }
}
