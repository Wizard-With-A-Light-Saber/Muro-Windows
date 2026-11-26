using System.Security.Cryptography;

namespace Muro.Core.Updates;

/// Proves an update came from whoever holds the release key. The release's SHA256SUMS.txt is signed
/// (ECDSA P-256) with a private key that never goes to GitHub, so someone who can replace the release files
/// still cannot make the app run their installer. A checksum alone would not do that.
public static class ReleaseSignature
{
    /// The public half (SubjectPublicKeyInfo, base64), printed by `muro sign-keygen`. While it is empty nothing
    /// verifies, so nothing is installed automatically.
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE3fHp5J8bJqqYlT9YHZGoFN1g2kxc6s1TOb9+qPfI4U5AH45Kb7nFiQwVR4RS4JZCWe1nZlBUU8UebvhQ/du4VA==";

    public const string SumsName = "SHA256SUMS.txt";
    public const string SignatureName = SumsName + ".sig";

    public static bool Verify(byte[] data, byte[] signature, string publicKey = PublicKey)
    {
        if (publicKey.Length == 0) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return false; }
    }

    /// The SHA-256 (hex) listed for exactly this file name, or null. Lines are "hash  name".
    public static string? HashFor(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && parts[1].TrimStart(' ', '*') == fileName) return parts[0];
        }
        return null;
    }

    /// A new key pair: the private half as PEM (encrypted when a password is given) and the public half.
    public static (string PrivatePem, string Public) CreateKey(string? password)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = string.IsNullOrEmpty(password)
            ? key.ExportPkcs8PrivateKeyPem()
            : new string(PemEncoding.Write("ENCRYPTED PRIVATE KEY", key.ExportEncryptedPkcs8PrivateKey(
                password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000))));
        return (pem, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    public static byte[] Sign(byte[] data, string privatePem, string? password)
    {
        using var key = ECDsa.Create();
        if (privatePem.Contains("ENCRYPTED")) key.ImportFromEncryptedPem(privatePem, password ?? "");
        else key.ImportFromPem(privatePem);
        return key.SignData(data, HashAlgorithmName.SHA256);
    }
}
