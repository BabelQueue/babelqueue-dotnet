using System.Collections.Generic;
using System.Text.Json;
using BabelQueue;

namespace BabelQueue.Schema;

/// <summary>
/// Materializes JSON into the same <c>object?</c> tree the envelope codec produces
/// (objects to <see cref="Dictionary{TKey,TValue}"/>, arrays to <see cref="List{T}"/>,
/// numbers to <c>long</c>/<c>double</c>, plus string/bool/null), so a parsed schema and a
/// decoded message <c>data</c> compare under the same types.
/// </summary>
public static class SchemaJson
{
    /// <summary>Parse any JSON document into its materialized <c>object?</c> tree.</summary>
    public static object? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ToValue(doc.RootElement);
    }

    /// <summary>Parse a JSON object into a materialized map.</summary>
    public static IReadOnlyDictionary<string, object?> ParseObject(string json)
    {
        if (Parse(json) is IReadOnlyDictionary<string, object?> map)
        {
            return map;
        }

        throw new BabelQueueException("schema: expected a JSON object");
    }

    private static object? ToValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => ToDictionary(e),
        JsonValueKind.Array => ToList(e),
        JsonValueKind.String => e.GetString(),
        // Cast keeps long boxed as long (the ternary would otherwise widen both to double).
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static Dictionary<string, object?> ToDictionary(JsonElement e)
    {
        var result = new Dictionary<string, object?>();
        foreach (var property in e.EnumerateObject())
        {
            result[property.Name] = ToValue(property.Value);
        }

        return result;
    }

    private static List<object?> ToList(JsonElement e)
    {
        var result = new List<object?>();
        foreach (var item in e.EnumerateArray())
        {
            result.Add(ToValue(item));
        }

        return result;
    }
}
