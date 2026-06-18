using System.Collections.Generic;

namespace BabelQueue.Schema;

/// <summary>In-memory <see cref="ISchemaProvider"/>, for tests and for embedding schemas in code.</summary>
public sealed class MapProvider : ISchemaProvider
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _schemas;

    /// <summary>Build a provider from URN to already-decoded JSON Schema maps.</summary>
    public MapProvider(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> schemas)
    {
        _schemas = new Dictionary<string, IReadOnlyDictionary<string, object?>>(schemas);
    }

    /// <summary>Build a provider from URN to raw JSON Schema strings, parsing each.</summary>
    public static MapProvider FromJson(IReadOnlyDictionary<string, string> raw)
    {
        var schemas = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        foreach (var entry in raw)
        {
            schemas[entry.Key] = SchemaJson.ParseObject(entry.Value);
        }

        return new MapProvider(schemas);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?>? SchemaFor(string urn) =>
        _schemas.TryGetValue(urn, out var schema) ? schema : null;
}
