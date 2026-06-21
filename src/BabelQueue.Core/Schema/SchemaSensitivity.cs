using System;
using System.Collections.Generic;

namespace BabelQueue.Schema;

/// <summary>
/// One property a schema marked <c>x-gdpr-sensitive</c>, located by its dotted path from the
/// schema root (array elements use the <c>field[]</c> segment the validator and
/// babelqueue-registry <c>compat</c> use). <see cref="Category"/> is the optional
/// <c>"x-gdpr-sensitive": "&lt;category&gt;"</c> string (e.g. <c>"email"</c>), or <c>""</c> when the
/// keyword was the boolean <c>true</c>.
/// </summary>
/// <param name="Path">The dotted path to the marked property (<c>""</c> for a root mark).</param>
/// <param name="Category">The optional free-form category, or <c>""</c>.</param>
public readonly record struct SensitivePath(string Path, string Category);

/// <summary>
/// Extracts the <c>x-gdpr-sensitive</c> marks (ADR-0030) from a per-URN JSON Schema — the
/// value-level counterpart to babelqueue-registry's governance inventory. The keyword is a
/// recognised-but-validation-neutral extension (<see cref="PayloadValidator"/> ignores it
/// entirely, so annotating a schema is never a breaking change, GR-1); the
/// <see cref="BabelQueue.Gdpr.Gdpr"/> helpers consume these paths to encrypt on produce and
/// decrypt on consume.
/// </summary>
/// <remarks>
/// Works directly on the materialized <c>object?</c> schema tree the codec/<see cref="SchemaJson"/>
/// produce — the same shape <see cref="PayloadValidator"/> walks — so the schema model is left
/// untouched. Mirrors the Go <c>schema.Schema.SensitivePaths</c> walk.
/// </remarks>
public static class SchemaSensitivity
{
    private const string Keyword = "x-gdpr-sensitive";

    /// <summary>
    /// Every property marked <c>x-gdpr-sensitive</c> in <paramref name="schema"/>, in sorted path
    /// order. Descends into nested objects (dotted paths such as <c>profile.full_name</c>) and
    /// array item schemas (<c>addresses[].line</c>); a mark on the root schema itself is reported
    /// with a path of <c>""</c>. Returns an empty list when nothing is marked.
    /// </summary>
    public static IReadOnlyList<SensitivePath> SensitivePaths(IReadOnlyDictionary<string, object?> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var paths = new List<SensitivePath>();
        Collect(schema, string.Empty, paths);
        paths.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return paths;
    }

    private static void Collect(IReadOnlyDictionary<string, object?> schema, string path, List<SensitivePath> into)
    {
        if (TryReadMark(schema, out var category))
        {
            into.Add(new SensitivePath(path, category));
        }

        if (schema.TryGetValue("properties", out var p)
            && p is IReadOnlyDictionary<string, object?> properties)
        {
            foreach (var member in properties)
            {
                if (member.Value is IReadOnlyDictionary<string, object?> child)
                {
                    Collect(child, Join(path, member.Key), into);
                }
            }
        }

        if (schema.TryGetValue("items", out var it)
            && it is IReadOnlyDictionary<string, object?> items)
        {
            Collect(items, path + "[]", into);
        }
    }

    /// <summary>
    /// Reads the <c>x-gdpr-sensitive</c> keyword: the boolean <c>true</c> marks the property with an
    /// empty category; a non-empty string marks it with that category. Any other shape — <c>false</c>,
    /// <c>""</c>, a number — leaves it unmarked. Mirrors the Go/registry parser.
    /// </summary>
    private static bool TryReadMark(IReadOnlyDictionary<string, object?> schema, out string category)
    {
        category = string.Empty;
        if (!schema.TryGetValue(Keyword, out var raw))
        {
            return false;
        }

        switch (raw)
        {
            case bool b:
                return b;
            case string s when s.Length > 0:
                category = s;
                return true;
            default:
                return false;
        }
    }

    private static string Join(string path, string key) =>
        path.Length == 0 ? key : path + "." + key;
}
