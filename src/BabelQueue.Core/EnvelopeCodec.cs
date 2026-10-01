using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BabelQueue;

/// <summary>
/// Builds, encodes and decodes the canonical BabelQueue envelope — the single .NET
/// implementation of the wire format. The shape is frozen as
/// <c>{job, trace_id, data, meta, attempts}</c> (schema version 1) so a .NET service
/// interoperates byte-for-byte with the PHP/Laravel, Python, Go, Node and Java SDKs
/// over any broker. Uses the in-box <see cref="System.Text.Json"/> — no dependencies.
/// </summary>
/// <remarks>Full spec: <see href="https://babelqueue.com"/>.</remarks>
public static class EnvelopeCodec
{
    /// <summary>The wire envelope schema version this core implements.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The value stamped into <c>meta.lang</c> for envelopes produced here.</summary>
    public const string SourceLang = "dotnet";

    // Canonical keys: decoded into typed fields, never captured as extras. `urn` is the
    // inbound alias of `job` (resolved on decode, never re-emitted).
    private static readonly HashSet<string> KnownRootKeys = new(StringComparer.Ordinal)
    {
        "job", "trace_id", "data", "meta", "attempts", "dead_letter", "urn",
    };

    private static readonly HashSet<string> KnownMetaKeys = new(StringComparer.Ordinal)
    {
        "id", "queue", "lang", "schema_version", "created_at",
    };

    // Non-canonical keys (message-envelope.md §10). Policy K-15: decode warns and drops
    // them (never captured as extras), encode never emits them.
    private static readonly HashSet<string> ForbiddenRootKeys = new(StringComparer.Ordinal)
    {
        "timestamp",
    };

    private static readonly HashSet<string> ForbiddenMetaKeys = new(StringComparer.Ordinal)
    {
        "max_retries", "attempts", "source", "ts",
    };

    // Relaxed encoder => slashes, non-ASCII and HTML chars stay literal (only ", \
    // and control characters are escaped), matching the other SDK cores.
    private static readonly JsonSerializerOptions EncodeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// Builds the canonical envelope for a <c>(urn, data)</c> pair. A fresh trace id
    /// is minted unless <paramref name="traceId"/> is non-blank (trace continuation);
    /// <c>attempts</c> starts at 0 and <c>meta</c> is stamped with a unique id, the
    /// source language, the schema version and a millisecond timestamp.
    /// </summary>
    /// <exception cref="BabelQueueException">If <paramref name="urn"/> is null or blank.</exception>
    public static Envelope Make(
        string urn,
        IReadOnlyDictionary<string, object?>? data = null,
        string queue = "default",
        string? traceId = null)
    {
        var resolvedUrn = (urn ?? string.Empty).Trim();
        if (resolvedUrn.Length == 0)
        {
            throw new BabelQueueException(
                "A polyglot message must expose a stable, non-empty URN so consumers "
                + "can identify it without any class name.");
        }

        var trace = (traceId ?? string.Empty).Trim();
        if (trace.Length == 0)
        {
            trace = Guid.NewGuid().ToString();
        }

        var payload = data is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(data);

        var meta = new Meta(
            Guid.NewGuid().ToString(),
            queue ?? "default",
            SourceLang,
            SchemaVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return new Envelope(resolvedUrn, trace, payload, meta, 0, null);
    }

    /// <summary>
    /// Builds the envelope from an <see cref="IPolyglotMessage"/>. If the message also
    /// implements <see cref="IHasTraceId"/> and returns a non-empty value, that trace
    /// id is reused.
    /// </summary>
    public static Envelope FromMessage(IPolyglotMessage message, string queue = "default")
    {
        var trace = message is IHasTraceId hasTrace ? hasTrace.GetBabelTraceId() : null;
        return Make(message.GetBabelUrn(), message.ToPayload(), queue, trace);
    }

    /// <summary>
    /// Encodes the envelope as compact UTF-8 JSON. Slashes and non-ASCII are left
    /// unescaped, matching the other SDK cores; the field order is canonical. Unknown
    /// keys carried in <see cref="Meta.Extras"/> / <see cref="Envelope.Extras"/> are
    /// written after the canonical fields (in their captured order); canonical names
    /// and the forbidden keys of message-envelope.md §10 are never emitted from extras.
    /// </summary>
    public static string Encode(Envelope envelope)
    {
        var meta = envelope.Meta;
        var metaMap = new Dictionary<string, object?>
        {
            ["id"] = meta?.Id,
            ["queue"] = meta?.Queue,
            ["lang"] = meta?.Lang,
            ["schema_version"] = meta?.SchemaVersion ?? 0,
            ["created_at"] = meta?.CreatedAt ?? 0L,
        };
        AppendExtras(metaMap, meta?.Extras, KnownMetaKeys, ForbiddenMetaKeys);

        var root = new Dictionary<string, object?>
        {
            ["job"] = envelope.Job,
            ["trace_id"] = envelope.TraceId,
            ["data"] = envelope.Data,
            ["meta"] = metaMap,
            ["attempts"] = envelope.Attempts,
        };

        if (envelope.DeadLetter is { } dl)
        {
            root["dead_letter"] = new Dictionary<string, object?>
            {
                ["reason"] = dl.Reason,
                ["error"] = dl.Error,
                ["exception"] = dl.Exception,
                ["failed_at"] = dl.FailedAt,
                ["original_queue"] = dl.OriginalQueue,
                ["attempts"] = dl.Attempts,
                ["lang"] = dl.Lang,
            };
        }

        AppendExtras(root, envelope.Extras, KnownRootKeys, ForbiddenRootKeys);

        return JsonSerializer.Serialize(root, EncodeOptions);
    }

    /// <summary>
    /// Parses a raw JSON body into an <see cref="Envelope"/>. Malformed or non-object
    /// input yields an empty envelope (so <see cref="Accepts"/> returns <c>false</c>);
    /// the <c>urn</c> inbound alias is resolved into <c>job</c>. Does not validate the
    /// contents — call <see cref="Accepts"/> first.
    /// </summary>
    /// <remarks>
    /// Unknown top-level and <c>meta</c> keys are kept in <see cref="Envelope.Extras"/> /
    /// <see cref="Meta.Extras"/> so a re-encode preserves them. A forbidden key
    /// (message-envelope.md §10) is dropped with a warning written to
    /// <see cref="Trace"/>; use <see cref="Decode(string, Action{string}?)"/> to observe
    /// warnings directly.
    /// </remarks>
    public static Envelope Decode(string raw) => Decode(raw, null);

    /// <summary>
    /// Parses a raw JSON body like <see cref="Decode(string)"/>, reporting each
    /// warning (a forbidden key from message-envelope.md §10, named by its JSON
    /// pointer such as <c>/meta/max_retries</c>) to <paramref name="onWarning"/>.
    /// When <paramref name="onWarning"/> is <c>null</c>, warnings go to
    /// <see cref="Trace.TraceWarning(string)"/>. Forbidden keys are dropped — never
    /// captured as extras — so they are never re-emitted.
    /// </summary>
    public static Envelope Decode(string raw, Action<string>? onWarning)
    {
        var warn = onWarning ?? (static message => Trace.TraceWarning(message));

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Empty();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Empty();
        }

        var job = GetString(root, "job");
        if (string.IsNullOrWhiteSpace(job))
        {
            var alias = GetString(root, "urn");
            if (alias is not null)
            {
                job = alias;
            }
        }

        return new Envelope(
            job,
            GetString(root, "trace_id"),
            GetObject(root, "data"),
            ParseMeta(root, warn),
            GetInt(root, "attempts", 0),
            ParseDeadLetter(root))
        {
            Extras = CaptureExtras(root, KnownRootKeys, ForbiddenRootKeys, "/", warn),
        };
    }

    /// <summary>The message URN — the canonical <c>job</c>, with the <c>urn</c> alias resolved by <see cref="Decode(string)"/>.</summary>
    public static string Urn(Envelope envelope) => envelope.Job?.Trim() ?? string.Empty;

    /// <summary>
    /// Whether a consumer should accept this envelope: rejects a missing URN, an
    /// unsupported <c>meta.schema_version</c>, missing <c>data</c> or a blank
    /// <c>trace_id</c> — the consumer-side counterpart to the producer JSON Schema.
    /// </summary>
    public static bool Accepts(Envelope envelope)
    {
        if (Urn(envelope).Length == 0)
        {
            return false;
        }
        if (envelope.Meta is null || envelope.Meta.SchemaVersion != SchemaVersion)
        {
            return false;
        }
        if (envelope.Data is null)
        {
            return false;
        }
        return !string.IsNullOrWhiteSpace(envelope.TraceId);
    }

    private static Envelope Empty() => new(null, null, null, null, 0, null);

    private static Meta? ParseMeta(JsonElement root, Action<string> warn)
    {
        if (Prop(root, "meta") is not { ValueKind: JsonValueKind.Object } m)
        {
            return null;
        }
        return new Meta(
            GetString(m, "id"),
            GetString(m, "queue"),
            GetString(m, "lang"),
            GetInt(m, "schema_version", 0),
            GetLong(m, "created_at", 0L))
        {
            Extras = CaptureExtras(m, KnownMetaKeys, ForbiddenMetaKeys, "/meta/", warn),
        };
    }

    /// <summary>
    /// Collects every non-canonical key of <paramref name="obj"/> as raw JSON, in
    /// document order. A forbidden key is reported (by JSON pointer) and dropped.
    /// Returns <c>null</c> when nothing is left to carry.
    /// </summary>
    private static Dictionary<string, JsonElement>? CaptureExtras(
        JsonElement obj,
        HashSet<string> known,
        HashSet<string> forbidden,
        string pointerPrefix,
        Action<string> warn)
    {
        Dictionary<string, JsonElement>? extras = null;
        foreach (var property in obj.EnumerateObject())
        {
            if (known.Contains(property.Name))
            {
                continue;
            }
            if (forbidden.Contains(property.Name))
            {
                warn($"BabelQueue: dropped forbidden envelope key '{pointerPrefix}{property.Name}' "
                    + "(non-canonical, message-envelope.md §10); it is ignored and never re-emitted.");
                continue;
            }
            extras ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            extras[property.Name] = property.Value;
        }
        return extras;
    }

    /// <summary>
    /// Appends carried extras after the canonical fields already in <paramref name="target"/>,
    /// skipping canonical names (the typed field always wins), forbidden keys and
    /// undefined elements.
    /// </summary>
    private static void AppendExtras(
        Dictionary<string, object?> target,
        Dictionary<string, JsonElement>? extras,
        HashSet<string> known,
        HashSet<string> forbidden)
    {
        if (extras is null)
        {
            return;
        }
        foreach (var (key, value) in extras)
        {
            if (known.Contains(key)
                || forbidden.Contains(key)
                || target.ContainsKey(key)
                || value.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }
            target[key] = value;
        }
    }

    private static DeadLetter? ParseDeadLetter(JsonElement root)
    {
        if (Prop(root, "dead_letter") is not { ValueKind: JsonValueKind.Object } d)
        {
            return null;
        }
        return new DeadLetter(
            GetString(d, "reason") ?? string.Empty,
            GetString(d, "error"),
            GetString(d, "exception"),
            GetLong(d, "failed_at", 0L),
            GetString(d, "original_queue") ?? string.Empty,
            GetInt(d, "attempts", 0),
            GetString(d, "lang") ?? string.Empty);
    }

    private static JsonElement? Prop(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) ? value : null;

    private static string? GetString(JsonElement obj, string name)
        => Prop(obj, name) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    private static int GetInt(JsonElement obj, string name, int fallback)
        => Prop(obj, name) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var i) ? i : fallback;

    private static long GetLong(JsonElement obj, string name, long fallback)
        => Prop(obj, name) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt64(out var l) ? l : fallback;

    private static Dictionary<string, object?>? GetObject(JsonElement obj, string name)
        => Prop(obj, name) is { ValueKind: JsonValueKind.Object } e ? ToDictionary(e) : null;

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
