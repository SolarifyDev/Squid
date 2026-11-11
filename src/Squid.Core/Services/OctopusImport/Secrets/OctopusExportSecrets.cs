using System.Security.Cryptography;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Decrypts values encrypted with an Octopus export password (sensitive variables, feed passwords).
/// Holds only the derived key, never the password. <see cref="None"/> is the null object used when
/// a session has no export password (sessions uploaded before export secrets were retained, or after
/// the password has been cleared) — every decrypt attempt then fails, which preserves the legacy
/// "sensitive values must be re-entered" behaviour.
/// </summary>
public sealed class OctopusExportSecrets
{
    public static OctopusExportSecrets None { get; } = new(null);

    private readonly byte[] _key;

    private OctopusExportSecrets(byte[] key) { _key = key; }

    public bool IsAvailable => _key != null;

    public static OctopusExportSecrets FromPassword(string password) => string.IsNullOrEmpty(password) ? None : new OctopusExportSecrets(OctopusExportCipher.DeriveKey(password));

    public bool TryDecrypt(string encryptedValue, out string plaintext)
    {
        plaintext = null;

        if (!IsAvailable) return false;

        return OctopusExportCipher.TryDecrypt(encryptedValue, _key, out plaintext);
    }

    public bool CanDecrypt(string encryptedValue)
    {
        if (!IsAvailable || !OctopusExportCipher.TryDecryptBytes(encryptedValue, _key, out var plaintext)) return false;

        CryptographicOperations.ZeroMemory(plaintext);

        return true;
    }

    public override string ToString() => IsAvailable ? "OctopusExportSecrets(available)" : "OctopusExportSecrets(none)";
}
