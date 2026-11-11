using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace BabelQueue.Schema;

/// <summary>
/// In-memory <see cref="ISchemaProvider"/>, for tests and for embedding schemas in code.
/// Construction is where a schema is first processed: every keyword outside the supported subset
/// (see <see cref="PayloadValidator.Warnings"/>) produces one <see cref="RegisteredSchemaWarning"/>
/// (the <see cref="SchemaWarning"/> plus the URN it was found under), handed to the hook
/// (<see cref="Trace.TraceWarning(string)"/> by default, like the envelope codec) and kept in
/// <see cref="Warnings"/>. Warnings never change a validation verdict.
/// </summary>
public sealed class MapProvider : ISchemaProvider
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _schemas;

    /// <summary>Build a provider from URN to already-decoded JSON Schema maps; warnings go to <see cref="Trace"/>.</summary>
    public MapProvider(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> schemas)
        : this(schemas, null)
    {
    }

    /// <summary>
    /// Build a provider from URN to already-decoded JSON Schema maps. Each registration warning (with its URN) goes to
    /// <paramref name="onWarning"/>, or to <see cref="Trace.TraceWarning(string)"/> when it is <c>null</c>.
    /// </summary>
    public MapProvider(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> schemas,
        Action<RegisteredSchemaWarning>? onWarning)
    {
        _schemas = new Dictionary<string, IReadOnlyDictionary<string, object?>>(schemas);
        var warn = onWarning ?? (static warning => Trace.TraceWarning(warning.Message));
        var collected = new List<RegisteredSchemaWarning>();
        // Iterate the supplied dictionary, not the copy: the order is that of the caller's dictionary.
        foreach (var entry in schemas)
        {
            if (entry.Value is null)
            {
                continue;
            }

            foreach (var warning in PayloadValidator.Warnings(entry.Value))
            {
                var registered = new RegisteredSchemaWarning(entry.Key, warning);
                collected.Add(registered);
                warn(registered);
            }
        }

        Warnings = collected;
    }

    /// <summary>
    /// The warnings produced when the schemas were registered, in the iteration order of the dictionary
    /// passed to the constructor (document order within one schema).
    /// </summary>
    public IReadOnlyList<RegisteredSchemaWarning> Warnings { get; }

    /// <summary>Build a provider from URN to raw JSON Schema strings, parsing each.</summary>
    public static MapProvider FromJson(IReadOnlyDictionary<string, string> raw) => FromJson(raw, null);

    /// <summary>Like <see cref="FromJson(IReadOnlyDictionary{string, string})"/>, with an explicit warning hook.</summary>
    public static MapProvider FromJson(IReadOnlyDictionary<string, string> raw, Action<RegisteredSchemaWarning>? onWarning)
    {
        var schemas = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        foreach (var entry in raw)
        {
            schemas[entry.Key] = SchemaJson.ParseObject(entry.Value);
        }

        return new MapProvider(schemas, onWarning);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?>? SchemaFor(string urn) =>
        _schemas.TryGetValue(urn, out var schema) ? schema : null;
}
