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
    long CreatedAt);
