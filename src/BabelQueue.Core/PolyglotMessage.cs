using System.Collections.Generic;

namespace BabelQueue;

/// <summary>
/// A message that can be produced as a polyglot envelope. Implement it on your own
/// types so <see cref="EnvelopeCodec.FromMessage"/> can build the canonical envelope
/// without ever leaking a language-specific class name onto the wire.
/// </summary>
public interface IPolyglotMessage
{
    /// <summary>The stable URN that identifies this message across languages.</summary>
    string GetBabelUrn();

    /// <summary>The pure-JSON payload (no class instances).</summary>
    IReadOnlyDictionary<string, object?> ToPayload();
}

/// <summary>
/// Optionally implemented alongside <see cref="IPolyglotMessage"/> to continue an
/// existing distributed trace instead of minting a fresh one.
/// </summary>
public interface IHasTraceId
{
    /// <summary>The trace id to reuse, or <c>null</c> to mint a new one.</summary>
    string? GetBabelTraceId();
}
