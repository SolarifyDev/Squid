using System.Security.Cryptography;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Decrypts values encrypted with an Octopus export password (sensitive variables, feed passwords).
/// Holds only the derived key, never the password. <see cref="None"/> is the null object used when
/// a session has no export password (sessions uploaded before export secrets were retained, or after
/// the password has been cleared) — every decrypt attempt then fails, which preserves the legacy
/// "sensitive values must be re-entered" behaviour. <see cref="Unreadable"/> and <see cref="NotRetained"/>
/// behave exactly like <see cref="None"/> for decryption, but record that a password WAS proven at upload,
/// so the import can say why it is missing: <see cref="Unreadable"/> = an envelope was written and cannot be
/// read back with the current master key (restoring the original key recovers it); <see cref="NotRetained"/> =
/// no master key existed at upload, so nothing was written and nothing can ever recover it.
/// </summary>
public sealed class OctopusExportSecrets
{
    public static OctopusExportSecrets None { get; } = new(null, false, false);

    public static OctopusExportSecrets Unreadable { get; } = new(null, true, false);

    public static OctopusExportSecrets NotRetained { get; } = new(null, false, true);

    private readonly byte[] _key;

    private OctopusExportSecrets(byte[] key, bool isUnreadable, bool isNotRetained)
    {
        _key = key;
        IsUnreadable = isUnreadable;
        IsNotRetained = isNotRetained;
    }

    public bool IsAvailable => _key != null;

    public bool IsUnreadable { get; }

    public bool IsNotRetained { get; }

    public static OctopusExportSecrets FromPassword(string password) => string.IsNullOrEmpty(password) ? None : new OctopusExportSecrets(OctopusExportCipher.DeriveKey(password), false, false);

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

    public override string ToString() => $"OctopusExportSecrets({Describe()})";

    private string Describe()
    {
        if (IsAvailable) return "available";
        if (IsUnreadable) return "unreadable";

        return IsNotRetained ? "not retained" : "none";
    }
}
