using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace BabelQueue.Schema;

/// <summary>
/// Validates a message's <c>data</c> block against a per-URN JSON Schema (ADR-0024). A
/// hand-rolled subset of Draft-07 (zero dependencies) whose verdicts match the Go, PHP,
/// Python, Node and Java validators and babelqueue-registry's <c>compat</c> linter. Supported
/// keywords: <c>type</c> (a name or an array of names, i.e. a union), <c>required</c>,
/// <c>properties</c>, <c>additionalProperties</c>, <c>items</c>, <c>enum</c>, <c>const</c>,
/// <c>minLength</c>, <c>maxLength</c>, <c>minimum</c>. A keyword outside the subset never changes
/// the verdict (it is evaluated as absent), but <see cref="Warnings"/> reports each occurrence so
/// registration can warn about it. <c>const</c>/<c>enum</c> compare numbers by mathematical value
/// (<c>1 == 1.0 == 1e0</c>), recursively, and never equate a boolean or a string with a number.
/// It works on the materialized <c>object?</c> tree the codec produces (<c>long</c>/<c>double</c>)
/// and tolerates any .NET numeric type (<c>int</c>, <c>decimal</c>, <c>BigInteger</c>, ...).
/// </summary>
public static class PayloadValidator
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyProps =
        new Dictionary<string, object?>();

    // Annotation keywords never warn; neither does any keyword starting with "x-" (for example the
    // ADR-0030 x-gdpr-sensitive).
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "title", "description", "examples", "default", "$comment",
    };

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "type", "required", "properties", "additionalProperties", "items", "enum", "const",
        "minLength", "maxLength", "minimum",
    };

    /// <summary>
    /// Every keyword in <paramref name="schema"/> outside the supported subset, one
    /// <see cref="SchemaWarning"/> per occurrence. The walk descends only into the values of
    /// <c>properties</c>, <c>items</c> and <c>additionalProperties</c> when each is an object, and
    /// never into the value of an unsupported keyword. Annotation keywords (<c>$schema</c>,
    /// <c>$id</c>, <c>title</c>, <c>description</c>, <c>examples</c>, <c>default</c>, <c>$comment</c>,
    /// <c>x-*</c>) are not unsupported. The verdict of <see cref="Validate"/> is unaffected.
    /// </summary>
    public static IReadOnlyList<SchemaWarning> Warnings(IReadOnlyDictionary<string, object?> schema)
    {
        var result = new List<SchemaWarning>();
        CollectWarnings(schema, string.Empty, result);
        return result;
    }

    private static void CollectWarnings(IReadOnlyDictionary<string, object?> schema, string pointer, List<SchemaWarning> result)
    {
        foreach (var keyword in schema.Keys)
        {
            if (!Supported.Contains(keyword) && !Annotations.Contains(keyword)
                && !keyword.StartsWith("x-", StringComparison.Ordinal))
            {
                result.Add(new SchemaWarning(pointer, keyword));
            }
        }

        if (schema.TryGetValue("properties", out var props) && props is IReadOnlyDictionary<string, object?> properties)
        {
            foreach (var property in properties)
            {
                if (property.Value is IReadOnlyDictionary<string, object?> child)
                {
                    CollectWarnings(child, pointer + "/properties/" + EscapePointer(property.Key), result);
                }
            }
        }

        if (schema.TryGetValue("items", out var it) && it is IReadOnlyDictionary<string, object?> items)
        {
            CollectWarnings(items, pointer + "/items", result);
        }

        if (schema.TryGetValue("additionalProperties", out var ap) && ap is IReadOnlyDictionary<string, object?> additional)
        {
            CollectWarnings(additional, pointer + "/additionalProperties", result);
        }
    }

    // RFC 6901: "~" -> "~0" first, then "/" -> "~1".
    private static string EscapePointer(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

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

        if (schema.TryGetValue("type", out var t) && t is IReadOnlyList<object?> union)
        {
            return CheckUnion(schema, union, value, path);
        }

        return CheckType(schema, t is string s ? s : string.Empty, value, path);
    }

    // A union is valid when the value matches ANY listed type; the keywords of the matching type
    // then apply to it. An empty union and an unknown type name constrain nothing, like an unknown
    // single `type`.
    private static string? CheckUnion(
        IReadOnlyDictionary<string, object?> schema, IReadOnlyList<object?> union, object? value, string path)
    {
        var sawType = false;
        string? shapeViolation = null;
        foreach (var member in union)
        {
            if (member is not string name)
            {
                continue;
            }

            sawType = true;
            var found = CheckType(schema, name, value, path);
            if (found is null)
            {
                return null;
            }

            // A type that matches the value's shape but fails one of its keywords (minLength,
            // minimum, nested properties...) explains the failure better than a bare mismatch.
            if (shapeViolation is null && MatchesType(name, value))
            {
                shapeViolation = found;
            }
        }

        return !sawType ? null : shapeViolation ?? Violation(path, "not_in_type_union");
    }

    private static bool MatchesType(string type, object? value) => type switch
    {
        "object" => value is IReadOnlyDictionary<string, object?>,
        "array" => value is IReadOnlyList<object?>,
        "string" => value is string,
        "integer" => IsInteger(value),
        "number" => IsNumber(value),
        "boolean" => value is bool,
        "null" => value is null,
        _ => true,
    };

    private static string? CheckType(IReadOnlyDictionary<string, object?> schema, string type, object? value, string path) =>
        type switch
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

    private static int CodePointLength(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }
        return count;
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

        // minLength counts Unicode code points (JSON Schema / ECMA-262), not UTF-16 units:
        // "çğü" is 3, an astral emoji is 1 — matching the other SDK validators.
        if (schema.TryGetValue("minLength", out var ml) && TryDouble(ml, out var min) && CodePointLength(str) < min)
        {
            return Violation(path, "below_min_length");
        }

        if (schema.TryGetValue("maxLength", out var xl) && TryDouble(xl, out var max) && CodePointLength(str) > max)
        {
            return Violation(path, "above_max_length");
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

    // JSON numbers materialize to long (integers) or double (fractions); a whole-valued number of
    // any representation (1.0, 1e2, 1.00m) still counts as an integer, matching the other SDKs.
    // bool is not numeric.
    private static bool IsInteger(object? value) =>
        value is long or int || (TryNormalize(value, out var n) && (n.Mantissa.IsZero || n.Exponent >= 0));

    private static bool IsNumber(object? value) => IsNumeric(value);

    private static bool IsNumeric(object? value) => value is sbyte or byte or short or ushort or int or uint
        or long or ulong or float or double or decimal or BigInteger;

    private static bool TryDouble(object? value, out double result)
    {
        switch (value)
        {
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            case BigInteger big:
                result = (double)big;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    // A number as mantissa * 10^exponent with the trailing zeros of the mantissa folded into the
    // exponent, so equal mathematical values have the same key whatever the .NET type: long 1,
    // double 1.0, double 1e0 and decimal 1.00m are all (1, 0); 2.5 and 2.50 are both (25, -1).
    // NaN and the infinities are not representable: they are not normalizable.
    private readonly record struct Normalized(BigInteger Mantissa, int Exponent);

    private static bool TryNormalize(object? value, out Normalized result)
    {
        result = default;
        string text;
        switch (value)
        {
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                break;
            case BigInteger big:
                text = big.ToString(CultureInfo.InvariantCulture);
                break;
            case decimal m:
                text = m.ToString(CultureInfo.InvariantCulture);
                break;
            case double d when double.IsFinite(d):
                text = d.ToString("R", CultureInfo.InvariantCulture);
                break;
            case float f when float.IsFinite(f):
                text = f.ToString("R", CultureInfo.InvariantCulture);
                break;
            default:
                return false;
        }

        // [-]digits[.digits][E[+-]digits]
        var exponent = 0;
        var e = text.IndexOf('E', StringComparison.Ordinal);
        if (e >= 0)
        {
            exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            exponent -= text.Length - dot - 1;
            text = text.Remove(dot, 1);
        }

        var mantissa = BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (mantissa.IsZero)
        {
            result = new Normalized(BigInteger.Zero, 0);
            return true;
        }

        while ((mantissa % 10).IsZero)
        {
            mantissa /= 10;
            exponent++;
        }

        result = new Normalized(mantissa, exponent);
        return true;
    }

    // JSON-Schema equality for const/enum: numbers by mathematical value (recursively inside
    // arrays and objects), objects by key set (order-free), arrays element by element, and nothing
    // crosses kinds (true != 1, "1" != 1, null equals only null).
    private static bool Equal(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (IsNumeric(a) && IsNumeric(b))
        {
            if (TryNormalize(a, out var na) && TryNormalize(b, out var nb))
            {
                return na == nb;
            }

            // NaN / infinity: only the same non-finite double equals itself.
            return TryDouble(a, out var da) && TryDouble(b, out var db) && da.Equals(db);
        }

        if (a is IReadOnlyDictionary<string, object?> da2 && b is IReadOnlyDictionary<string, object?> db2)
        {
            if (da2.Count != db2.Count)
            {
                return false;
            }

            foreach (var entry in da2)
            {
                if (!db2.TryGetValue(entry.Key, out var other) || !Equal(entry.Value, other))
                {
                    return false;
                }
            }

            return true;
        }

        if (a is IReadOnlyList<object?> la && b is IReadOnlyList<object?> lb)
        {
            if (la.Count != lb.Count)
            {
                return false;
            }

            for (var i = 0; i < la.Count; i++)
            {
                if (!Equal(la[i], lb[i]))
                {
                    return false;
                }
            }

            return true;
        }

        return (a is string || a is bool) && a.GetType() == b.GetType() && a.Equals(b);
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
