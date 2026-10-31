using System.Globalization;
using System.Text.Json;

namespace BabelQueue;

/// <summary>The outcome of classifying a raw message body: it decodes to an envelope with an identity, or it is poison.</summary>
/// <remarks>
/// <see cref="Poison"/> is deliberately the zero value, so a <c>default</c> <see cref="BodyVerdictResult"/> (an unassigned
/// field, a <c>TryX(out ...)</c> that never ran) is poison, never a body that was silently classified as fine.
/// </remarks>
public enum BodyVerdictKind
{
    /// <summary>The body cannot be decoded, or decodes without a valid identity. The zero value.</summary>
    Poison = 0,

    /// <summary>The body decodes into an envelope that carries a valid identity (<c>job</c> / <c>urn</c>).</summary>
    Ok = 1,
}

/// <summary>A <see cref="BodyVerdict.Classify(string?)"/> result: the verdict and, for a poison body, why.</summary>
/// <param name="Kind">The verdict.</param>
/// <param name="Cause">Why the body is poison, or <c>null</c> when it is <see cref="BodyVerdictKind.Ok"/>.</param>
public readonly record struct BodyVerdictResult(BodyVerdictKind Kind, string? Cause)
{
    /// <summary>Whether the body decodes into an envelope with a valid identity.</summary>
    public bool IsOk => Kind == BodyVerdictKind.Ok;

    /// <summary>Whether the body is poison.</summary>
    public bool IsPoison => Kind == BodyVerdictKind.Poison;
}

/// <summary>
/// Classifies a raw message body as <see cref="BodyVerdictKind.Ok"/> or <see cref="BodyVerdictKind.Poison"/>
/// (ADR-0036 decision 4; ADR-0032 section 6; error-handling.md section 3) — the cross-SDK <c>identity_verdict</c>
/// verdict. Unlike <see cref="EnvelopeCodec.Decode(string)"/>, which is deliberately lenient about the type of a
/// known member (a wrong type maps to its default), this check is strict, so a consumer can park a malformed body
/// on the poison route instead of handing a half-decoded envelope to a handler.
/// </summary>
/// <remarks>
/// <see cref="EnvelopeCodec.Decode(string)"/> is unchanged; this is an additional entry point. A body is
/// <b>poison</b> when:
/// <list type="bullet">
/// <item>it is not exactly one JSON value (no BOM, trailing tokens, malformed or non-finite numbers) or is not a
/// JSON object (a bare <c>null</c> is empty, hence has no identity);</item>
/// <item>a known member has the wrong type: <c>trace_id</c>, <c>meta.id|queue|lang</c> and
/// <c>dead_letter.reason|error|exception|original_queue|lang</c> are string-or-null; <c>attempts</c>,
/// <c>meta.schema_version|created_at</c> and <c>dead_letter.failed_at|attempts</c> are integer-or-null (an integer
/// literal that fits int64); <c>data</c>, <c>meta</c> and <c>dead_letter</c> are object-or-null, and numbers inside
/// <c>data</c> are finite doubles. Member names are case-sensitive and every occurrence of a duplicated key is
/// checked; any other member is only checked for syntax;</item>
/// <item>it nests deeper than <see cref="CoreMaxDepth"/> (64) levels, which <see cref="EnvelopeCodec.Decode(string)"/>
/// cannot carry. The top-level object is level 1. This limit is specific to the .NET core: Go and Node accept 10000
/// levels, PHP 511, Java 256, and Python depends on the interpreter (about 1000 on CPython 3.9, far more on 3.14+), so a
/// body nested between 65 and 10000 levels can be <see cref="BodyVerdictKind.Ok"/> in another SDK (recorded in
/// ADR-0036 decision 4, "SDK note", owner decision 2026-10-31);</item>
/// <item><see cref="EnvelopeCodec.Decode(string)"/> would throw on it. System.Text.Json cannot materialise a string or a
/// member name that holds a lone UTF-16 surrogate escape (<c>"\ud800"</c>), anywhere in the body, and
/// <see cref="EnvelopeCodec.Decode(string)"/> lets that <see cref="InvalidOperationException"/> escape. So a
/// <see cref="BodyVerdictKind.Ok"/> body is one <see cref="EnvelopeCodec.Decode(string)"/> returns for. This is a
/// platform limit: the Go, Python, Node and Java classifiers accept such a body (Go reads it as U+FFFD). Deliberate
/// (owner decision 2026-10-31, ADR-0036 decision 4 "SDK note"): it keeps the guarantee above;</item>
/// <item>it carries no valid identity (ADR-0032 section 6): the LAST top-level <c>job</c> is used when it is a
/// string that is not blank (only ASCII whitespace is blank); otherwise the LAST <c>urn</c> alias when it is a
/// non-blank string. A <c>job</c> or <c>urn</c> that is not a string is ignored.</item>
/// </list>
/// A well-typed envelope that fails <see cref="EnvelopeCodec.Accepts(Envelope)"/> (a missing <c>data</c> or an
/// unsupported <c>schema_version</c>) is <b>not</b> poison here.
/// </remarks>
public static class BodyVerdict
{
    /// <summary>The JSON nesting limit of <see cref="EnvelopeCodec.Decode(string)"/> (the System.Text.Json default).</summary>
    public const int CoreMaxDepth = 64;

    private const int ParseMaxDepth = 10_000;

    private enum Kind { String, Integer, Object, Data }

    private static readonly Dictionary<string, Kind> MetaMembers = new(StringComparer.Ordinal)
    {
        ["id"] = Kind.String,
        ["queue"] = Kind.String,
        ["lang"] = Kind.String,
        ["schema_version"] = Kind.Integer,
        ["created_at"] = Kind.Integer,
    };

    private static readonly Dictionary<string, Kind> DeadLetterMembers = new(StringComparer.Ordinal)
    {
        ["reason"] = Kind.String,
        ["error"] = Kind.String,
        ["exception"] = Kind.String,
        ["original_queue"] = Kind.String,
        ["lang"] = Kind.String,
        ["failed_at"] = Kind.Integer,
        ["attempts"] = Kind.Integer,
    };

    private static readonly Dictionary<string, Kind> TopMembers = new(StringComparer.Ordinal)
    {
        ["trace_id"] = Kind.String,
        ["attempts"] = Kind.Integer,
        ["data"] = Kind.Data,
        ["meta"] = Kind.Object,
        ["dead_letter"] = Kind.Object,
    };

    /// <summary>Classifies <paramref name="body"/>, the raw message body (a JSON text).</summary>
    /// <param name="body">The raw body; <c>null</c> is poison.</param>
    /// <returns>The verdict and, for a poison body, the cause.</returns>
    public static BodyVerdictResult Classify(string? body)
    {
        var cause = PoisonCause(body);
        return cause is null
            ? new BodyVerdictResult(BodyVerdictKind.Ok, null)
            : new BodyVerdictResult(BodyVerdictKind.Poison, cause);
    }

    /// <summary>Why <paramref name="body"/> is poison, or <c>null</c> when it decodes with a valid identity.</summary>
    /// <param name="body">The raw body.</param>
    public static string? PoisonCause(string? body)
    {
        if (body is null)
        {
            return "the body is null.";
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = ParseMaxDepth });
        }
        catch (JsonException e)
        {
            return "not valid JSON: " + e.Message;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "expected a JSON object.";
            }

            try
            {
                var problem = CheckObject(root, string.Empty, TopMembers);
                if (problem is not null)
                {
                    return problem;
                }
                if (ExceedsCoreDepth(root))
                {
                    return "nesting is deeper than the core codec can decode (" + CoreMaxDepth + " levels).";
                }
                MaterialiseStrings(root);
                return HasIdentity(root) ? null : "no valid identity: neither 'job' nor 'urn' is a non-blank string.";
            }
            catch (InvalidOperationException e)
            {
                // System.Text.Json cannot materialise a member name or string holding a lone UTF-16
                // surrogate escape (e.g. "\ud800"); the core codec cannot carry it either.
                return "a string is not representable (lone UTF-16 surrogate escape): " + e.Message;
            }
        }
    }

    // ADR-0032 section 6: the last `job` wins; a non-string or blank `job` falls back to the last `urn`,
    // which must be a non-blank string. Blank is the closed ASCII whitespace set (ADR-0031 (c)).
    private static bool HasIdentity(JsonElement root)
    {
        string? job = null;
        string? urn = null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "job", StringComparison.Ordinal))
            {
                job = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
            else if (string.Equals(property.Name, "urn", StringComparison.Ordinal))
            {
                urn = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }
        return !EnvelopeCodec.IsBlank(job) || !EnvelopeCodec.IsBlank(urn);
    }

    private static string? CheckObject(JsonElement obj, string path, Dictionary<string, Kind> members)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!members.TryGetValue(property.Name, out var kind))
            {
                continue; // unknown member: syntax only
            }
            var value = property.Value;
            var name = path + property.Name;
            switch (kind)
            {
                case Kind.String:
                    if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    {
                        return $"'{name}' must be a string.";
                    }
                    break;
                case Kind.Integer:
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        break;
                    }
                    if (value.ValueKind != JsonValueKind.Number || !IsInt64Literal(value.GetRawText()))
                    {
                        return $"'{name}' must be an integer.";
                    }
                    break;
                case Kind.Data:
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        break;
                    }
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        return $"'{name}' must be a JSON object.";
                    }
                    if (!AllNumbersFinite(value))
                    {
                        return $"'{name}' holds a number that is not a finite double.";
                    }
                    break;
                case Kind.Object:
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        break;
                    }
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        return $"'{name}' must be a JSON object.";
                    }
                    var inner = string.Equals(property.Name, "meta", StringComparison.Ordinal) ? MetaMembers : DeadLetterMembers;
                    var problem = CheckObject(value, name + ".", inner);
                    if (problem is not null)
                    {
                        return problem;
                    }
                    break;
            }
        }
        return null;
    }

    // An integer literal (optional minus, no fraction or exponent) that fits int64.
    private static bool IsInt64Literal(string raw) =>
        long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
        && raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0;

    // Iterative on purpose: the parse accepts 10000 levels of nesting, which a recursive walk cannot survive
    // on a consumer thread's stack (a StackOverflow cannot be caught).
    private static bool AllNumbersFinite(JsonElement root)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    if (!(double.TryParse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        && double.IsFinite(d)))
                    {
                        return false;
                    }
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        pending.Push(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        pending.Push(item);
                    }
                    break;
            }
        }
        return true;
    }

    // Reads every member name and string value once, as EnvelopeCodec.Decode does. A lone surrogate escape makes
    // System.Text.Json throw InvalidOperationException, which Classify turns into a poison verdict.
    // Iterative, like the other walks: the parse accepts 10000 levels.
    private static void MaterialiseStrings(JsonElement root)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    _ = element.GetString();
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        _ = property.Name;
                        pending.Push(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        pending.Push(item);
                    }
                    break;
            }
        }
    }

    private static bool ExceedsCoreDepth(JsonElement root)
    {
        var pending = new Stack<(JsonElement Element, int Depth)>();
        pending.Push((root, 1));
        while (pending.Count > 0)
        {
            var (element, depth) = pending.Pop();
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (depth > CoreMaxDepth)
                    {
                        return true;
                    }
                    foreach (var property in element.EnumerateObject())
                    {
                        pending.Push((property.Value, depth + 1));
                    }
                    break;
                case JsonValueKind.Array:
                    if (depth > CoreMaxDepth)
                    {
                        return true;
                    }
                    foreach (var item in element.EnumerateArray())
                    {
                        pending.Push((item, depth + 1));
                    }
                    break;
            }
        }
        return false;
    }
}
