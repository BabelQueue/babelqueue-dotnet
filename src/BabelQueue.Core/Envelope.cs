using System.Collections.Generic;

namespace BabelQueue;

/// <summary>
/// The canonical BabelQueue wire message: a strict, language-neutral JSON shape
/// (<c>{job, trace_id, data, meta, attempts}</c>) that every SDK produces and
/// consumes identically — no language-specific serialization on the wire.
/// </summary>
/// <remarks>
/// Build one with <see cref="EnvelopeCodec.Make"/>, render it with
/// <see cref="EnvelopeCodec.Encode"/>, and parse inbound bytes with
/// <see cref="EnvelopeCodec.Decode"/>. The record is immutable; use a
/// <c>with</c> expression (or <see cref="DeadLetters.Annotate"/>) to derive copies.
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
    DeadLetter? DeadLetter);
