using System;
using System.Collections.Generic;

namespace BabelQueue.Schema;

/// <summary>
/// Validates a message's <c>data</c> block against a per-URN JSON Schema (ADR-0024). A
/// hand-rolled subset of Draft-07 (zero dependencies) whose verdicts match the Go, PHP,
/// Python, Node and Java validators and babelqueue-registry's <c>compat</c> linter. Supported
/// keywords: <c>type</c>, <c>required</c>, <c>properties</c>, <c>additionalProperties</c>,
/// <c>items</c>, <c>enum</c>, <c>const</c>, <c>minLength</c>, <c>minimum</c>; unknown keywords
/// are ignored. It works on the materialized <c>object?</c> tree the codec produces.
/// </summary>
public static class PayloadValidator
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyProps =
        new Dictionary<string, object?>();

    /// <summary>
    /// The first violation of <paramref name="value"/> against <paramref name="schema"/> as
    /// <c>"&lt;json-pointer&gt;: &lt;reason&gt;"</c>, or <c>null</c> when it conforms.
    /// </summary>
    public static string? Validate(IReadOnlyDictionary<string, object?> schema, object? value) =>
        ValidateNode(schema, value, string.Empty);

    private static string? ValidateNode(IReadOnlyDictionary<string, object?> schema, object? value, string path)
    {
        if (schema.TryGetValue("const", out var constValue) && !Equal(value, constValue))
        {
            return Violation(path, "wrong_const");
        }

        if (schema.TryGetValue("enum", out var enumObj)
            && enumObj is IReadOnlyList<object?> enumValues
            && !Contains(enumValues, value))
        {
            return Violation(path, "not_in_enum");
        }

        var type = schema.TryGetValue("type", out var t) && t is string s ? s : string.Empty;
        return type switch
        {
            "object" => CheckObject(schema, value, path),
            "array" => CheckArray(schema, value, path),
            "string" => CheckString(schema, value, path),
            "integer" => IsInteger(value) ? CheckMinimum(schema, value, path) : Violation(path, "not_an_integer"),
            "number" => IsNumber(value) ? CheckMinimum(schema, value, path) : Violation(path, "not_a_number"),
            "boolean" => value is bool ? null : Violation(path, "not_a_boolean"),
            "null" => value is null ? null : Violation(path, "not_null"),
            _ => null,
        };
    }

    private static string? CheckObject(IReadOnlyDictionary<string, object?> schema, object? value, string path)
    {
        if (value is not IReadOnlyDictionary<string, object?> obj)
        {
            return Violation(path, "not_an_object");
        }

        if (schema.TryGetValue("required", out var req) && req is IReadOnlyList<object?> required)
        {
            foreach (var key in required)
            {
                if (key is string name && !obj.ContainsKey(name))
                {
                    return Violation(Join(path, name), "missing_required");
                }
            }
        }

        var properties = schema.TryGetValue("properties", out var p)
            && p is IReadOnlyDictionary<string, object?> props
            ? props
            : EmptyProps;
        var additionalAllowed = !(schema.TryGetValue("additionalProperties", out var ap) && ap is false);

        foreach (var member in obj)
        {
            if (properties.TryGetValue(member.Key, out var ps) && ps is IReadOnlyDictionary<string, object?> propSchema)
            {
                var found = ValidateNode(propSchema, member.Value, Join(path, member.Key));
                if (found is not null)
                {
                    return found;
                }
            }
            else if (!additionalAllowed)
            {
                return Violation(Join(path, member.Key), "additional_not_allowed");
            }
        }

        return null;
    }

    private static string? CheckArray(IReadOnlyDictionary<string, object?> schema, object? value, string path)
    {
        if (value is not IReadOnlyList<object?> list)
        {
            return Violation(path, "not_an_array");
        }

        if (!(schema.TryGetValue("items", out var it) && it is IReadOnlyDictionary<string, object?> items))
        {
            return null;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var found = ValidateNode(items, list[i], path + "[" + i + "]");
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? CheckString(IReadOnlyDictionary<string, object?> schema, object? value, string path)
    {
        if (value is not string str)
        {
            return Violation(path, "not_a_string");
        }

        if (schema.TryGetValue("minLength", out var ml) && ml is long min && str.Length < min)
        {
            return Violation(path, "below_min_length");
        }

        return null;
    }

    private static string? CheckMinimum(IReadOnlyDictionary<string, object?> schema, object? value, string path)
    {
        if (schema.TryGetValue("minimum", out var m)
            && TryDouble(m, out var min)
            && TryDouble(value, out var actual)
            && actual < min)
        {
            return Violation(path, "below_minimum");
        }

        return null;
    }

    // JSON numbers materialize to long (integers) or double (fractions); a whole-valued double
    // still counts as an integer, matching the other SDKs. bool is not long/double.
    private static bool IsInteger(object? value) => value switch
    {
        long or int => true,
        double d => !double.IsInfinity(d) && d == Math.Floor(d),
        _ => false,
    };

    private static bool IsNumber(object? value) => value is long or int or double;

    private static bool TryDouble(object? value, out double result)
    {
        switch (value)
        {
            case long l:
                result = l;
                return true;
            case int i:
                result = i;
                return true;
            case double d:
                result = d;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    // Type-aware equality: a long never equals a double, true never equals 1 — matching the
    // strict comparisons in the other SDK validators.
    private static bool Equal(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.GetType() == b.GetType() && a.Equals(b);
    }

    private static bool Contains(IReadOnlyList<object?> values, object? value)
    {
        foreach (var item in values)
        {
            if (Equal(value, item))
            {
                return true;
            }
        }

        return false;
    }

    private static string Violation(string path, string reason) =>
        (path.Length == 0 ? "<root>" : path) + ": " + reason;

    private static string Join(string path, string key) =>
        path.Length == 0 ? key : path + "." + key;
}
