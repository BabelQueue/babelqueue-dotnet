using System;
using System.Security.Cryptography;

namespace BabelQueue.Gdpr;

/// <summary>
/// A reference <see cref="ICipher"/> built ONLY on the in-box
/// <see cref="System.Security.Cryptography.AesGcm"/>: AES-GCM authenticated encryption with a
/// fresh random 12-byte nonce per call. The wire layout is <c>nonce(12) || ciphertext || tag(16)</c>,
/// base64-encoded so it drops straight into a JSON string. The key is the <b>caller's</b> — this
/// type performs no key management, rotation or derivation; bind a KMS-backed
/// <see cref="ICipher"/> for that.
/// </summary>
/// <remarks>
/// AES-256-GCM is used when the key is 32 bytes (recommended); 16- and 24-byte keys select
/// AES-128/192-GCM. GCM authenticates the ciphertext, so <see cref="Decrypt"/> rejects any tampered
/// or wrong-key input with an exception (it never returns corrupt plaintext). It is
/// zero-dependency and safe for concurrent use (the key is read-only after construction; a fresh
/// <see cref="System.Security.Cryptography.AesGcm"/> instance is used per operation).
/// </remarks>
public sealed class AesGcmCipher : ICipher
{
    private const int NonceSize = 12; // AesGcm.NonceByteSizes recommended size; matches every SDK.
    private const int TagSize = 16;   // AesGcm.TagByteSizes max size; GCM auth tag.

    private readonly byte[] _key;

    /// <summary>
    /// Build an AES-GCM reference cipher from a raw symmetric key. The key length selects the AES
    /// variant: 32 bytes → AES-256-GCM (recommended), 24 → AES-192, 16 → AES-128.
    /// </summary>
    /// <param name="key">A 16-, 24- or 32-byte symmetric key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is not 16, 24, or 32 bytes.</exception>
    public AesGcmCipher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException(
                $"AES key must be 16, 24, or 32 bytes (got {key.Length}).", nameof(key));
        }

        _key = (byte[])key.Clone();
    }

    /// <summary>
    /// Seal <paramref name="plaintext"/> with a fresh random nonce and base64-encode
    /// <c>nonce || ciphertext || tag</c>. Implements <see cref="ICipher.Encrypt"/>.
    /// </summary>
    /// <param name="plaintext">The canonical JSON bytes of one field value.</param>
    /// <returns>The base64 ciphertext string.</returns>
    public string Encrypt(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var cipherText = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, cipherText, tag);
        }

        var sealed_ = new byte[NonceSize + cipherText.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, sealed_, 0, NonceSize);
        Buffer.BlockCopy(cipherText, 0, sealed_, NonceSize, cipherText.Length);
        Buffer.BlockCopy(tag, 0, sealed_, NonceSize + cipherText.Length, TagSize);

        return Convert.ToBase64String(sealed_);
    }

    /// <summary>
    /// Reverse <see cref="Encrypt"/>: base64-decode, split off the prepended nonce and trailing tag,
    /// and open the GCM ciphertext. A wrong key or tampered input fails GCM authentication and
    /// throws (never corrupt plaintext). Implements <see cref="ICipher.Decrypt"/>.
    /// </summary>
    /// <param name="ciphertext">A base64 string previously returned by <see cref="Encrypt"/>.</param>
    /// <returns>The original field-value JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ciphertext"/> is null.</exception>
    /// <exception cref="FormatException"><paramref name="ciphertext"/> is not valid base64.</exception>
    /// <exception cref="ArgumentException"><paramref name="ciphertext"/> is too short to hold a nonce and tag.</exception>
    /// <exception cref="CryptographicException">Authentication failed (wrong key / tampered input).</exception>
    public byte[] Decrypt(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        var raw = Convert.FromBase64String(ciphertext);
        if (raw.Length < NonceSize + TagSize)
        {
            throw new ArgumentException("Ciphertext is shorter than a nonce and tag.", nameof(ciphertext));
        }

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipherLength = raw.Length - NonceSize - TagSize;
        var cipherText = new byte[cipherLength];

        Buffer.BlockCopy(raw, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(raw, NonceSize, cipherText, 0, cipherLength);
        Buffer.BlockCopy(raw, NonceSize + cipherLength, tag, 0, TagSize);

        var plaintext = new byte[cipherLength];
        using (var aes = new AesGcm(_key, TagSize))
        {
            // Open throws CryptographicException on wrong key / tampered ciphertext / wrong tag.
            aes.Decrypt(nonce, cipherText, tag, plaintext);
        }

        return plaintext;
    }
}
