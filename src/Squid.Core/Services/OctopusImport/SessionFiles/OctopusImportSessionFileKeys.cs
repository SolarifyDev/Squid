using System.Security.Cryptography;
using Squid.Core.Services.Security;

namespace Squid.Core.Services.OctopusImport.SessionFiles;

/// <summary>
/// The per-session data key that seals an import session's files, wrapped once with the variable-encryption master key. Wrapping once
/// matters: the master-key envelope derives its key with PBKDF2 on every call, far too slow per chunk. Like the export password it does
/// not go through <see cref="IAtRestSecretProtector"/> (idempotent and read-both, both wrong here) and is bound to its purpose with
/// <see cref="PurposeTag"/>, so an envelope written for anything else under the same master key never unwraps as a file key.
/// </summary>
public static class OctopusImportSessionFileKeys
{
    /// <summary>Scope passed to the encryption service (log context / legacy KDF), not a domain separator. Persisted format — pinned.</summary>
    public const int KdfScope = -1002;

    /// <summary>Prepended to the key inside the envelope; an unwrapped plaintext without it is refused. Persisted format — pinned.</summary>
    public const string PurposeTag = "squid.octopus-import.session-file-key.v1\n";

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(OctopusImportSessionFileCodec.KeyBytes);

    public static string Wrap(IVariableEncryptionService encryption, byte[] key) => encryption.EncryptAsync(PurposeTag + Convert.ToBase64String(key), KdfScope);

    /// <summary>The key, or null when the envelope is not a file-key envelope or cannot be decrypted (rotated master key, tampering).</summary>
    public static byte[] UnwrapOrNull(IVariableEncryptionService encryption, string envelope)
    {
        if (string.IsNullOrEmpty(envelope) || !envelope.StartsWith(VariableEncryptionService.EncryptionPrefixV2, StringComparison.Ordinal)) return null;

        try
        {
            return KeyOrNull(encryption.Decrypt(envelope, KdfScope));
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning("Octopus import session file key could not be decrypted at rest ({ExceptionType}); the session's files cannot be read", ex.InnerException?.GetType().Name ?? ex.GetType().Name);
            return null;
        }
    }

    private static byte[] KeyOrNull(string plaintext)
    {
        if (plaintext == null || !plaintext.StartsWith(PurposeTag, StringComparison.Ordinal)) return null;

        var key = new byte[OctopusImportSessionFileCodec.KeyBytes];

        return Convert.TryFromBase64String(plaintext[PurposeTag.Length..], key, out var written) && written == key.Length ? key : null;
    }
}
