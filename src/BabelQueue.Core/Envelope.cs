using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BabelQueue;

/// <summary>
/// The canonical BabelQueue wire message: a strict, language-neutral JSON shape
/// (<c>{job, trace_id, data, meta, attempts}</c>) that every SDK produces and
/// consumes identically — no language-specific serialization on the wire.
/// </summary>
/// <remarks>
/// Build one with <see cref="EnvelopeCodec.Make"/>, render it with
/// <see cref="EnvelopeCodec.Encode"/>, and parse inbound bytes with
/// <see cref="EnvelopeCodec.Decode(string)"/>. The record is immutable; use a
/// <c>with</c> expression (or <see cref="DeadLetters.Annotate"/>) to derive copies;
/// a <c>with</c> copy carries <see cref="Extras"/> along, so unknown keys survive
/// every re-emit (retry, dead-letter, redrive, outbox relay).
/// </remarks>
/// <param name="Job">The message URN (never a class name).</param>
/// <param name="TraceId">Correlation id, preserved across every hop.</param>
/// <param name="Data">The pure-JSON payload.</param>
/// <param name="Meta">The immutable metadata block.</param>
/// <param name="Attempts">The top-level transport retry counter.</param>
/// <param name="DeadLetter">The dead-letter block, or <c>null</c> until dead-lettered.</param>
public sealed record Envelope(
    string? Job,
    string? TraceId,
    IReadOnlyDictionary<string, object?>? Data,
    Meta? Meta,
    int Attempts,
    DeadLetter? DeadLetter)
{
    /// <summary>
    /// Unknown top-level keys captured by <see cref="EnvelopeCodec.Decode(string)"/>,
    /// keyed by name and kept as raw JSON. <see cref="EnvelopeCodec.Encode"/> writes
    /// them back after the canonical fields, so a decode → re-encode never drops a
    /// forward-compatible extension. Never holds a canonical or forbidden key.
    /// <c>null</c> when the message carried none.
    /// <para>
    /// Treat as read-only: a <c>with</c> copy shares this dictionary with the original, so
    /// mutating it in place changes both. To change extras, assign a new dictionary
    /// (<c>env with { Extras = new(env.Extras!) { ["k"] = v } }</c>).
    /// </para>
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extras { get; init; }
}
