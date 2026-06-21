using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using BabelQueue.Schema;

namespace BabelQueue.Gdpr;

/// <summary>
/// The RUNTIME half of ADR-0030: SDK-side field-level encryption of the <c>data</c> fields a
/// registry declared <c>x-gdpr-sensitive</c>. babelqueue-registry only <i>declares</i> and
/// <i>audits</i> sensitivity (and offers one-way masking for safe logging); these helpers
/// <i>enforce</i> it on the wire — a producer encrypts each marked leaf before publish, a consumer
/// decrypts it after decode. The .NET port of the Go <c>gdpr</c> reference package.
/// </summary>
/// <remarks>
/// The contract is deliberately tight and mirrors the Go reference:
/// <list type="bullet">
/// <item><b>The envelope stays frozen (GR-1).</b> <see cref="Protect"/> mutates only <i>values</i>
/// inside <c>data</c>: a sensitive leaf's value becomes a ciphertext <b>string</b>. It never adds,
/// renames, removes or retypes an envelope field; <c>meta.schema_version</c> stays <c>1</c>;
/// <c>trace_id</c> is untouched (GR-4). A JSON string is still pure JSON (GR-3), so any SDK can
/// carry the envelope even without the key.</item>
/// <item><b>Zero heavy dependencies (GR-7).</b> The crypto is a caller-provided
/// <see cref="ICipher"/>; the bundled <see cref="AesGcmCipher"/> is in-box only.</item>
/// </list>
/// The sensitive paths come from the SAME per-URN schema the validation path loads — extract them
/// with <see cref="SchemaSensitivity.SensitivePaths"/>. <see cref="Protect"/>/<see cref="Unprotect"/>
/// are standalone helpers the caller invokes, so the feature is strictly opt-in. Validate
/// <b>cleartext</b> — before <see cref="Protect"/> on the producer, after <see cref="Unprotect"/>
/// on the consumer — because a schema constraining a sensitive field would otherwise reject the
/// ciphertext string.
/// </remarks>
public static class Gdpr
{
    // Same relaxed, compact encoder the envelope codec uses, so a leaf value is canonically encoded
    // before encryption — the encoding is what makes the round-trip exact and cross-SDK consistent.
    private static readonly JsonSerializerOptions FieldEncodeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// Encrypt, in place, every value in <paramref name="data"/> located at a path
    /// <paramref name="schema"/> marked <c>x-gdpr-sensitive</c> — the producer-side step, run after
    /// building <c>data</c> and before encode/publish. Each marked leaf's value is canonically
    /// JSON-encoded and replaced by <paramref name="cipher"/>'s ciphertext <b>string</b>; the
    /// envelope frame and non-sensitive fields are untouched (GR-1).
    /// </summary>
    /// <param name="data">The mutable <c>data</c> map to protect (the codec/SchemaJson materialization). May be null (no-op).</param>
    /// <param name="schema">The per-URN JSON Schema carrying the marks. May be null (no-op).</param>
    /// <param name="cipher">The caller's field cipher. May be null (no-op).</param>
    /// <remarks>
    /// A marked path absent from <paramref name="data"/> is skipped (not an error) — schemas evolve
    /// and a message need not carry every optional field. A whole-object/array mark encrypts the
    /// entire sub-value as one ciphertext string. On a cipher failure the underlying exception
    /// propagates and <paramref name="data"/> is left partially protected; treat a thrown
    /// <see cref="Protect"/> as fatal for that message (do not publish it).
    /// </remarks>
    public static void Protect(
        IDictionary<string, object?>? data,
        IReadOnlyDictionary<string, object?>? schema,
        ICipher? cipher) =>
        Walk(data, schema, cipher, EncryptLeaf);

    /// <summary>
    /// The consumer-side inverse of <see cref="Protect"/>: decrypt, in place, every value in
    /// <paramref name="data"/> at an <c>x-gdpr-sensitive</c> path, restoring the original JSON value
    /// byte-for-byte. Run it after decode and before the handler reads <c>data</c>.
    /// </summary>
    /// <param name="data">The mutable decoded <c>data</c> map. May be null (no-op).</param>
    /// <param name="schema">The per-URN JSON Schema carrying the marks. May be null (no-op).</param>
    /// <param name="cipher">The caller's field cipher. May be null (no-op).</param>
    /// <remarks>
    /// An absent path is skipped. A leaf that is NOT a string — never protected, or a re-run after a
    /// successful <see cref="Unprotect"/> — is left as-is, so re-invoking is safe (idempotent for
    /// non-string leaves). A string the cipher cannot open (wrong key, tampered, or not a
    /// ciphertext) throws <see cref="ProtectedFieldException"/> — fail the message (retry /
    /// dead-letter) rather than process unreadable PII.
    /// </remarks>
    public static void Unprotect(
        IDictionary<string, object?>? data,
        IReadOnlyDictionary<string, object?>? schema,
        ICipher? cipher) =>
        Walk(data, schema, cipher, DecryptLeaf);

    /// <summary>
    /// Transforms one sensitive leaf value (encrypt or decrypt). Returns the new value and a flag
    /// that is false when the slot should be left alone (absent / non-string in Unprotect).
    /// </summary>
    private delegate (object? value, bool replace) LeafOp(string path, object? value, ICipher cipher);

    // Drives a LeafOp over every x-gdpr-sensitive path the schema declares, resolving each path
    // against data itself (NOT by re-walking the schema over the value), so it touches exactly the
    // declared leaves — non-sensitive siblings are never read or copied.
    private static void Walk(
        IDictionary<string, object?>? data,
        IReadOnlyDictionary<string, object?>? schema,
        ICipher? cipher,
        LeafOp op)
    {
        if (data is null || schema is null || cipher is null)
        {
            return;
        }

        foreach (var sp in SchemaSensitivity.SensitivePaths(schema))
        {
            ApplyAtPath(data, ParsePath(sp.Path), 0, sp.Path, cipher, op);
        }
    }

    // One step of a sensitive path: a named object key, optionally with array-descent.
    // "addresses[].line" parses to [{Key:"addresses", Array:true}, {Key:"line"}].
    private readonly record struct Segment(string Key, bool Array);

    // Splits a path ("email", "profile.full_name", "addresses[].line") into segments. The "[]"
    // marker binds to the segment it trails, signalling array descent. A root mark (path "") yields
    // no segments — not addressable in a data object, so Walk skips it.
    private static Segment[] ParsePath(string path)
    {
        if (path.Length == 0)
        {
            return Array.Empty<Segment>();
        }

        var parts = path.Split('.');
        var segs = new Segment[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var key = parts[i];
            if (key.EndsWith("[]", StringComparison.Ordinal))
            {
                segs[i] = new Segment(key[..^2], true);
            }
            else
            {
                segs[i] = new Segment(key, false);
            }
        }

        return segs;
    }

    // Resolves segs against the current node and runs op on the leaf(s). Descends objects by key
    // and, when a segment is an array, fans out over every element. An absent key or a type mismatch
    // (a path that does not exist in this particular message) is skipped silently — schemas describe
    // the union of possible shapes; a given message need not contain every field.
    private static void ApplyAtPath(object? node, Segment[] segs, int index, string fullPath, ICipher cipher, LeafOp op)
    {
        if (index >= segs.Length)
        {
            return; // root mark or exhausted path with no leaf key — nothing addressable in data.
        }

        if (node is not IDictionary<string, object?> obj)
        {
            return; // expected an object here but the message has something else — skip.
        }

        var seg = segs[index];
        if (!obj.TryGetValue(seg.Key, out var child))
        {
            return; // absent field — skip (not an error).
        }

        var last = index == segs.Length - 1;

        if (seg.Array)
        {
            if (child is not IList<object?> arr)
            {
                return; // declared array but message has a non-array — skip.
            }

            for (var i = 0; i < arr.Count; i++)
            {
                if (last)
                {
                    var (value, replace) = op(fullPath, arr[i], cipher);
                    if (replace)
                    {
                        arr[i] = value;
                    }
                }
                else
                {
                    ApplyAtPath(arr[i], segs, index + 1, fullPath, cipher, op);
                }
            }

            return;
        }

        if (last)
        {
            var (value, replace) = op(fullPath, child, cipher);
            if (replace)
            {
                obj[seg.Key] = value;
            }

            return;
        }

        ApplyAtPath(child, segs, index + 1, fullPath, cipher, op);
    }

    // Canonically JSON-encodes one field value and replaces it with the cipher's ciphertext string.
    // The JSON encoding is what makes the round-trip exact: DecryptLeaf re-materializes the same
    // decoded-JSON value (long/double for numbers, Dictionary for objects, …) the codec produces.
    private static (object?, bool) EncryptLeaf(string path, object? value, ICipher cipher)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, FieldEncodeOptions);
        return (cipher.Encrypt(plaintext), true);
    }

    // Reverses EncryptLeaf. A non-string leaf is left untouched (replace=false) so Unprotect is safe
    // to re-run on already-cleartext data; a string that fails to open or to JSON-decode throws
    // ProtectedFieldException so the consumer fails the message rather than handling unreadable PII.
    private static (object?, bool) DecryptLeaf(string path, object? value, ICipher cipher)
    {
        if (value is not string ciphertext)
        {
            return (null, false); // already cleartext or never protected — leave as-is.
        }

        byte[] plaintext;
        try
        {
            plaintext = cipher.Decrypt(ciphertext);
        }
        catch (Exception ex) when (ex is not ProtectedFieldException)
        {
            throw new ProtectedFieldException(path, ex);
        }

        try
        {
            return (Materialize(plaintext), true);
        }
        catch (JsonException ex)
        {
            throw new ProtectedFieldException(path, ex);
        }
    }

    // Materializes decrypted JSON bytes into the same object? tree the envelope codec/SchemaJson
    // produce (Dictionary / List / long / double / string / bool / null), so a protected→unprotected
    // round-trip restores data byte-for-byte under the same .NET types.
    private static object? Materialize(byte[] json)
    {
        var reader = new Utf8JsonReader(json);
        using var doc = JsonDocument.ParseValue(ref reader);
        return ToValue(doc.RootElement);
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
