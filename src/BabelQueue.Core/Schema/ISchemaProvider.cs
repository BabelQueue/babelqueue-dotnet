using System.Collections.Generic;

namespace BabelQueue.Schema;

/// <summary>
/// A source of per-URN <c>data</c> schemas, keyed on the message URN (ADR-0024). Returns the
/// decoded JSON Schema for a URN's <c>data</c> block, or <c>null</c> when none is registered —
/// in which case the caller skips validation (the feature is opt-in). The reference
/// <see cref="MapProvider"/> is in-memory; the I/O-free core ships no file-based provider, so a
/// .NET app reads its babelqueue-registry <c>registry.json</c> and passes the schemas in.
/// </summary>
public interface ISchemaProvider
{
    /// <summary>The decoded JSON Schema registered for <paramref name="urn"/>, or null.</summary>
    IReadOnlyDictionary<string, object?>? SchemaFor(string urn);
}
