namespace BabelQueue.Gdpr;

/// <summary>
/// The field-level protection primitive the <b>caller provides</b> (ADR-0030) — a seam onto a KMS,
/// Vault transit, an HSM, a tokenisation service, or the reference <see cref="AesGcmCipher"/>.
/// <see cref="Gdpr.Protect"/> runs <see cref="Encrypt"/> over every <c>x-gdpr-sensitive</c> leaf's
/// value (after it is canonically JSON-encoded); <see cref="Gdpr.Unprotect"/> runs
/// <see cref="Decrypt"/> to restore it. Keeping this an interface is what holds GR-7: the core
/// package never pulls a crypto / KMS dependency — only a caller who binds a concrete backend does.
/// </summary>
/// <remarks>
/// Contract for an implementation:
/// <list type="bullet">
/// <item><see cref="Encrypt"/> takes the canonical JSON bytes of one field value and returns the
/// ciphertext as a <b>string</b> valid for placement inside a JSON document (the
/// <see cref="AesGcmCipher"/> reference returns base64, which is). The same plaintext MAY encrypt
/// to different strings each call (a random nonce/IV is expected and good).</item>
/// <item><see cref="Decrypt"/> is the exact inverse: given a string <see cref="Encrypt"/> produced,
/// it returns the original JSON bytes byte-for-byte. A string it did not produce, or one produced
/// under a different key, MUST throw rather than return silent garbage, so a wrong-key consume
/// fails loudly.</item>
/// <item>Both MUST be safe for concurrent use; a producer/consumer fans the same cipher across
/// threads.</item>
/// </list>
/// </remarks>
public interface ICipher
{
    /// <summary>
    /// Protect one field value (its canonical JSON bytes) and return a JSON-safe ciphertext string.
    /// </summary>
    /// <param name="plaintext">The canonical JSON bytes of one field value.</param>
    /// <returns>The ciphertext as a JSON-safe string.</returns>
    string Encrypt(byte[] plaintext);

    /// <summary>
    /// Reverse <see cref="Encrypt"/>, returning the original field-value JSON bytes. MUST throw on a
    /// wrong key, tampered input, or a string this cipher did not produce.
    /// </summary>
    /// <param name="ciphertext">A string previously returned by <see cref="Encrypt"/>.</param>
    /// <returns>The original field-value JSON bytes.</returns>
    byte[] Decrypt(string ciphertext);
}
