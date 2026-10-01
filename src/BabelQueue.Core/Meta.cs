using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BabelQueue;

/// <summary>The immutable per-message metadata block of an <see cref="Envelope"/>.</summary>
/// <param name="Id">A unique identifier for this specific message.</param>
/// <param name="Queue">The logical queue the message was produced for.</param>
/// <param name="Lang">The source SDK language (e.g. <c>"dotnet"</c>).</param>
/// <param name="SchemaVersion">The wire envelope schema version.</param>
/// <param name="CreatedAt">Creation time in Unix milliseconds, UTC.</param>
public sealed record Meta(
    string? Id,
    string? Queue,
    string? Lang,
    int SchemaVersion,
    long CreatedAt)
{
    /// <summary>
    /// Unknown <c>meta</c> keys captured by <see cref="EnvelopeCodec.Decode(string)"/>,
    /// kept as raw JSON and re-emitted by <see cref="EnvelopeCodec.Encode"/> after the
    /// canonical meta fields. Never holds a canonical or forbidden key. <c>null</c>
    /// when the message carried none.
    /// <para>
    /// Treat as read-only: a <c>with</c> copy shares this dictionary with the original, so
    /// mutating it in place changes both — assign a new dictionary instead.
    /// </para>
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extras { get; init; }
}
