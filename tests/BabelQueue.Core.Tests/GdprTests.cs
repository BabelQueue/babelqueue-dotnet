using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using BabelQueue;
using BabelQueue.Gdpr;
using BabelQueue.Schema;
using Xunit;

// The Gdpr class lives in the Gdpr namespace, so the bare name `Gdpr` is ambiguous (CS0118);
// alias the type — the same trap a consumer hits, documented in the README/CHANGELOG.
using GdprFields = BabelQueue.Gdpr.Gdpr;

namespace BabelQueue.Tests;

public class GdprTests
{
    // A 32-byte key → AES-256-GCM.
    private static byte[] Key(byte fill = 7) => Filled(32, fill);

    private static byte[] Filled(int length, byte fill)
    {
        var key = new byte[length];
        Array.Fill(key, fill);
        return key;
    }

    private static IReadOnlyDictionary<string, object?> Schema(string raw) => SchemaJson.ParseObject(raw);

    // A schema marking email (root prop), profile.full_name (nested) and addresses[].line (array item).
    private const string SensitiveSchema =
        "{\"type\":\"object\"," +
        "\"properties\":{" +
        "\"email\":{\"type\":\"string\",\"x-gdpr-sensitive\":\"email\"}," +
        "\"order_id\":{\"type\":\"integer\"}," +
        "\"profile\":{\"type\":\"object\",\"properties\":{" +
        "\"full_name\":{\"type\":\"string\",\"x-gdpr-sensitive\":true}," +
        "\"nickname\":{\"type\":\"string\"}}}," +
        "\"addresses\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"line\":{\"type\":\"string\",\"x-gdpr-sensitive\":true}," +
        "\"city\":{\"type\":\"string\"}}}}}}";

    // ---- AesGcmCipher -------------------------------------------------------

    [Fact]
    public void AesGcmCipherRoundTrips()
    {
        var cipher = new AesGcmCipher(Key());
        var plaintext = Encoding.UTF8.GetBytes("\"alice@example.com\"");

        var sealed_ = cipher.Encrypt(plaintext);
        Assert.NotEqual(Convert.ToBase64String(plaintext), sealed_); // not plaintext-in-base64
        Assert.Equal(plaintext, cipher.Decrypt(sealed_));
    }

    [Fact]
    public void AesGcmCipherUsesRandomNoncePerCall()
    {
        var cipher = new AesGcmCipher(Key());
        var plaintext = Encoding.UTF8.GetBytes("\"same\"");

        var a = cipher.Encrypt(plaintext);
        var b = cipher.Encrypt(plaintext);

        Assert.NotEqual(a, b);                       // random nonce → different ciphertext
        Assert.Equal(plaintext, cipher.Decrypt(a));  // both still decrypt
        Assert.Equal(plaintext, cipher.Decrypt(b));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void AesGcmCipherAcceptsValidKeySizes(int size)
    {
        var cipher = new AesGcmCipher(Filled(size, 3));
        var plaintext = Encoding.UTF8.GetBytes("\"x\"");
        Assert.Equal(plaintext, cipher.Decrypt(cipher.Encrypt(plaintext)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(31)]
    [InlineData(33)]
    public void AesGcmCipherRejectsInvalidKeySizes(int size)
    {
        Assert.Throws<ArgumentException>(() => new AesGcmCipher(Filled(size, 1)));
    }

    [Fact]
    public void AesGcmCipherWrongKeyFailsAuthentication()
    {
        var sealed_ = new AesGcmCipher(Key(1)).Encrypt(Encoding.UTF8.GetBytes("\"secret\""));
        // AuthenticationTagMismatchException derives from CryptographicException.
        Assert.ThrowsAny<CryptographicException>(() => new AesGcmCipher(Key(2)).Decrypt(sealed_));
    }

    [Fact]
    public void AesGcmCipherTamperedCiphertextFailsAuthentication()
    {
        var cipher = new AesGcmCipher(Key());
        var sealed_ = cipher.Encrypt(Encoding.UTF8.GetBytes("\"secret\""));

        var raw = Convert.FromBase64String(sealed_);
        raw[^1] ^= 0xFF; // flip a tag bit
        var tampered = Convert.ToBase64String(raw);

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(tampered));
    }

    [Fact]
    public void AesGcmCipherRejectsTooShortInput()
    {
        var cipher = new AesGcmCipher(Key());
        Assert.Throws<ArgumentException>(() => cipher.Decrypt(Convert.ToBase64String(new byte[4])));
    }

    // ---- SchemaSensitivity --------------------------------------------------

    [Fact]
    public void SensitivePathsCollectsNestedArrayAndCategory()
    {
        var paths = SchemaSensitivity.SensitivePaths(Schema(SensitiveSchema));

        // Sorted ordinal: addresses[].line, email, profile.full_name
        Assert.Equal(3, paths.Count);
        Assert.Equal(new SensitivePath("addresses[].line", string.Empty), paths[0]);
        Assert.Equal(new SensitivePath("email", "email"), paths[1]);
        Assert.Equal(new SensitivePath("profile.full_name", string.Empty), paths[2]);
    }

    [Fact]
    public void SensitivePathsReportsRootMark()
    {
        var paths = SchemaSensitivity.SensitivePaths(Schema("{\"type\":\"string\",\"x-gdpr-sensitive\":true}"));
        Assert.Single(paths);
        Assert.Equal(string.Empty, paths[0].Path);
    }

    [Fact]
    public void SensitivePathsIgnoresFalseEmptyAndNonStringMarks()
    {
        var schema = Schema(
            "{\"type\":\"object\",\"properties\":{" +
            "\"a\":{\"type\":\"string\",\"x-gdpr-sensitive\":false}," +
            "\"b\":{\"type\":\"string\",\"x-gdpr-sensitive\":\"\"}," +
            "\"c\":{\"type\":\"string\",\"x-gdpr-sensitive\":1}}}");
        Assert.Empty(SchemaSensitivity.SensitivePaths(schema));
    }

    [Fact]
    public void SensitiveKeywordIsValidationNeutral()
    {
        // The keyword never makes a value valid or invalid: validating with and without it agrees.
        var withMark = Schema("{\"type\":\"string\",\"minLength\":2,\"x-gdpr-sensitive\":true}");
        var without = Schema("{\"type\":\"string\",\"minLength\":2}");

        Assert.Equal(PayloadValidator.Validate(without, "ok"), PayloadValidator.Validate(withMark, "ok"));
        Assert.Equal(PayloadValidator.Validate(without, "x"), PayloadValidator.Validate(withMark, "x"));
        Assert.Null(PayloadValidator.Validate(withMark, "ok")); // valid
        Assert.NotNull(PayloadValidator.Validate(withMark, "x")); // below minLength, marked or not
    }

    // ---- Protect / Unprotect ------------------------------------------------

    private static Dictionary<string, object?> SampleData() => new()
    {
        ["email"] = "alice@example.com",
        ["order_id"] = 42L,
        ["profile"] = new Dictionary<string, object?>
        {
            ["full_name"] = "Alice Smith",
            ["nickname"] = "ally",
        },
        ["addresses"] = new List<object?>
        {
            new Dictionary<string, object?> { ["line"] = "1 Main St", ["city"] = "Springfield" },
            new Dictionary<string, object?> { ["line"] = "2 Elm St", ["city"] = "Shelbyville" },
        },
    };

    [Fact]
    public void ProtectThenUnprotectRestoresDataByteForByte()
    {
        var schema = Schema(SensitiveSchema);
        var cipher = new AesGcmCipher(Key());

        var env = EnvelopeCodec.Make("urn:babel:orders:created", SampleData(), "orders");
        var original = EnvelopeCodec.Encode(env);

        var data = (IDictionary<string, object?>)env.Data!;
        GdprFields.Protect(data, schema, cipher);

        // Sensitive values are now ciphertext strings; non-sensitive ones are untouched.
        Assert.IsType<string>(data["email"]);
        Assert.NotEqual("alice@example.com", data["email"]);
        Assert.Equal(42L, data["order_id"]);
        var profile = (IDictionary<string, object?>)data["profile"]!;
        Assert.IsType<string>(profile["full_name"]);
        Assert.Equal("ally", profile["nickname"]);

        // The protected envelope still encodes as pure JSON, decodes, and is accepted with
        // schema_version 1 and trace_id preserved.
        var protectedBody = EnvelopeCodec.Encode(env);
        var decoded = EnvelopeCodec.Decode(protectedBody);
        Assert.True(EnvelopeCodec.Accepts(decoded));
        Assert.Equal(1, decoded.Meta!.SchemaVersion);
        Assert.Equal(env.TraceId, decoded.TraceId);

        // Unprotect the decoded copy → re-encode → byte-for-byte equal to the original cleartext.
        GdprFields.Unprotect((IDictionary<string, object?>)decoded.Data!, schema, cipher);
        Assert.Equal(original, EnvelopeCodec.Encode(decoded));
    }

    [Fact]
    public void RoundTripPreservesNumberAndBooleanTypes()
    {
        // A whole-object mark encrypts the sub-value; restoring it must keep long/double/bool/null.
        var schema = Schema("{\"type\":\"object\",\"properties\":{\"payload\":{\"x-gdpr-sensitive\":true}}}");
        var cipher = new AesGcmCipher(Key());

        var data = new Dictionary<string, object?>
        {
            ["payload"] = new Dictionary<string, object?>
            {
                ["count"] = 7L,
                ["ratio"] = 0.5,
                ["flag"] = true,
                ["empty"] = null,
                ["items"] = new List<object?> { 1L, 2L, 3L },
            },
        };

        GdprFields.Protect(data, schema, cipher);
        Assert.IsType<string>(data["payload"]);

        GdprFields.Unprotect(data, schema, cipher);
        var payload = (IDictionary<string, object?>)data["payload"]!;
        Assert.Equal(7L, payload["count"]);
        Assert.Equal(0.5, payload["ratio"]);
        Assert.Equal(true, payload["flag"]);
        Assert.Null(payload["empty"]);
        Assert.Equal(new List<object?> { 1L, 2L, 3L }, payload["items"]);
    }

    [Fact]
    public void ProtectSkipsAbsentMarkedFields()
    {
        var schema = Schema(SensitiveSchema);
        var cipher = new AesGcmCipher(Key());

        // Only order_id present — every sensitive path is absent and must be skipped silently.
        var data = new Dictionary<string, object?> { ["order_id"] = 1L };
        GdprFields.Protect(data, schema, cipher);

        Assert.Single(data);
        Assert.Equal(1L, data["order_id"]);
    }

    [Fact]
    public void UnprotectLeavesNonStringLeafUntouchedAndIsIdempotent()
    {
        // A sensitive leaf whose cleartext value is NOT a string: a whole-object mark.
        var schema = Schema("{\"type\":\"object\",\"properties\":{\"payload\":{\"x-gdpr-sensitive\":true}}}");
        var cipher = new AesGcmCipher(Key());

        var data = new Dictionary<string, object?>
        {
            ["payload"] = new Dictionary<string, object?> { ["count"] = 1L },
        };

        // Never protected → the non-string (object) leaf is left as-is (not decrypted).
        GdprFields.Unprotect(data, schema, cipher);
        Assert.IsAssignableFrom<IDictionary<string, object?>>(data["payload"]);

        // Protect → Unprotect → Unprotect: the second pass sees a non-string leaf and no-ops.
        GdprFields.Protect(data, schema, cipher);
        GdprFields.Unprotect(data, schema, cipher);
        GdprFields.Unprotect(data, schema, cipher); // idempotent on the restored non-string leaf
        Assert.Equal(1L, ((IDictionary<string, object?>)data["payload"]!)["count"]);
    }

    [Fact]
    public void UnprotectWrongKeyThrowsTypedException()
    {
        var schema = Schema(SensitiveSchema);
        var data = SampleData();

        GdprFields.Protect(data, schema, new AesGcmCipher(Key(1)));

        var ex = Assert.Throws<ProtectedFieldException>(() =>
            GdprFields.Unprotect(data, schema, new AesGcmCipher(Key(2))));
        Assert.Equal("addresses[].line", ex.Path); // first sensitive path (sorted ordinal) that fails
        Assert.IsAssignableFrom<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public void NullArgumentsAreNoOps()
    {
        var schema = Schema(SensitiveSchema);
        var cipher = new AesGcmCipher(Key());
        var data = SampleData();

        GdprFields.Protect(null, schema, cipher);
        GdprFields.Protect(data, null, cipher);
        GdprFields.Protect(data, schema, null);

        Assert.Equal("alice@example.com", data["email"]); // unchanged by any no-op call
    }

    [Fact]
    public void TypeMismatchedPathsAreSkipped()
    {
        var schema = Schema(SensitiveSchema);
        var cipher = new AesGcmCipher(Key());

        // profile is a string (schema says object) and addresses is an object (schema says array):
        // both mismatches are skipped, email is still protected.
        var data = new Dictionary<string, object?>
        {
            ["email"] = "a@b.com",
            ["profile"] = "not-an-object",
            ["addresses"] = new Dictionary<string, object?> { ["line"] = "x" },
        };

        GdprFields.Protect(data, schema, cipher);
        Assert.IsType<string>(data["email"]);
        Assert.NotEqual("a@b.com", data["email"]);
        Assert.Equal("not-an-object", data["profile"]);
        Assert.Equal("x", ((IDictionary<string, object?>)data["addresses"]!)["line"]);
    }
}
