// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.BricsPay.Configuration;

namespace Bhengu.Finance.Payments.BricsPay.Internals;

/// <summary>
/// Signs BRICS Pay requests with the merchant's private key; BRICS Pay verifies with the public key
/// registered at onboarding. The signature is the base64 of the asymmetric signature over the UTF-8
/// payload (the JSON body for POST, or the query string for GET), digested with SHA-256, and is passed
/// as the <c>signature</c> URL query parameter.
/// <para>
/// ECDSA signatures are emitted in DER (RFC 3279) form, the common interop encoding. The exact wire
/// details BRICS Pay expects (DER vs IEEE-P1363, GET-parameter canonicalisation) are confirmed against a
/// live terminal at onboarding — this provider is <c>DocsOnly</c> until then.
/// </para>
/// </summary>
internal static class BricsPaySigner
{
    public static string Sign(string payload, BricsPayOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PrivateKeyPem);

        var data = Encoding.UTF8.GetBytes(payload);
        return options.SignatureAlgorithm switch
        {
            BricsPaySignatureAlgorithm.Rsa => SignRsa(data, options.PrivateKeyPem),
            _ => SignEcdsa(data, options.PrivateKeyPem)
        };
    }

    private static string SignEcdsa(byte[] data, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var sig = ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(sig);
    }

    private static string SignRsa(byte[] data, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(sig);
    }
}
