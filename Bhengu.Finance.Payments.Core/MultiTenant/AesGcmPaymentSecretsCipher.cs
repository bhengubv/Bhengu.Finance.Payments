// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Security.Cryptography;

namespace Bhengu.Finance.Payments.Core.MultiTenant;

/// <summary>
/// Default <see cref="IPaymentSecretsCipher"/> — AES-256-GCM with 96-bit nonce + 128-bit tag.
/// Key is supplied via constructor (32 bytes, raw — NOT base64 / hex). Suitable as the in-process
/// fallback when a KMS-backed implementation isn't wired; production deployments should override
/// with an envelope-encryption adapter that wraps a per-request data key under a KMS master key.
/// </summary>
public sealed class AesGcmPaymentSecretsCipher : IPaymentSecretsCipher
{
    private const int NonceSize = 12;   // 96 bits — GCM recommended
    private const int TagSize = 16;     // 128 bits — GCM max

    private readonly byte[] _key;

    /// <summary>Construct with a 32-byte symmetric key. Throws if the key isn't exactly 32 bytes.</summary>
    public AesGcmPaymentSecretsCipher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
            throw new ArgumentException("AES-256-GCM key MUST be exactly 32 bytes.", nameof(key));
        _key = (byte[])key.Clone();  // defensive copy — caller can't mutate after the fact
    }

    /// <inheritdoc />
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        Span<byte> nonce = stackalloc byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        // Wire format: [nonce | tag | ciphertext]
        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(output.AsSpan(0, NonceSize));
        tag.CopyTo(output.AsSpan(NonceSize, TagSize));
        ciphertext.CopyTo(output.AsSpan(NonceSize + TagSize));
        return output;
    }

    /// <inheritdoc />
    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData)
    {
        if (ciphertext.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext too short to contain nonce + tag.");

        var nonce = ciphertext.Slice(0, NonceSize);
        var tag = ciphertext.Slice(NonceSize, TagSize);
        var cipherBody = ciphertext.Slice(NonceSize + TagSize);

        var plaintext = new byte[cipherBody.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipherBody, tag, plaintext, associatedData);
        return plaintext;
    }
}
