// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.MultiTenant;

/// <summary>
/// Pluggable symmetric cipher for tenant credential encryption-at-rest. Used by
/// <see cref="ITenantPaymentSecretsStore"/> implementations that fetch encrypted blobs from
/// storage and need to decrypt them before binding to options.
///
/// <para>Default implementation is AES-256-GCM with key from configuration; production
/// deployments should swap for an envelope-encryption KMS adapter (AWS KMS / Azure Key Vault /
/// GCP KMS / HashiCorp Vault Transit).</para>
///
/// <para>The cipher MUST be authenticated (GCM, not CBC). Tenant credentials are bearer-token
/// material — modification in transit through the storage layer is a compromise.</para>
/// </summary>
public interface IPaymentSecretsCipher
{
    /// <summary>Encrypt a plaintext credential blob for storage.</summary>
    /// <param name="plaintext">UTF-8 bytes of the plaintext (e.g. JSON-serialised options).</param>
    /// <param name="associatedData">Bound context — typically tenant id + options type name — verified on decrypt.</param>
    /// <returns>Opaque ciphertext blob suitable for storage. Contains nonce + tag + ciphertext.</returns>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData);

    /// <summary>Decrypt a previously-encrypted ciphertext blob.</summary>
    /// <param name="ciphertext">Blob returned by a prior <see cref="Encrypt"/> call.</param>
    /// <param name="associatedData">MUST match the value passed to <see cref="Encrypt"/> — protects against tenant-mixup attacks where ciphertext is moved between rows.</param>
    /// <returns>Original plaintext.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">Authentication tag mismatch (tampered ciphertext or wrong associated data).</exception>
    byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData);
}
