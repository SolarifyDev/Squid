using System.Security.Cryptography;
using Squid.Core.Halibut.DataStreamStorage.Exceptions;
using Squid.Core.Services.Security;

namespace Squid.Core.Halibut.DataStreamStorage;

public interface IDataStreamKeyProtector
{
    byte[] NewKey();

    /// <summary>The key sealed with the variable-encryption master key. Throws when the master key is unavailable: nothing is ever stored unprotected.</summary>
    string Wrap(byte[] key);

    /// <summary>The key, or throws <see cref="DataStreamUnreadableException"/> when the envelope is not a data-stream key envelope or cannot be decrypted (rotated master key, tampering).</summary>
    byte[] Unwrap(string wrapped, Guid streamId);
}

/// <summary>
/// Seals the per-store data key with the master key through <see cref="IVariableEncryptionService"/> (the V2 envelope every at-rest secret uses). One wrap per
/// store call, never per chunk: the envelope derives its key with PBKDF2 on every call, far too slow for megabyte chunks. Like the import session file key it
/// does not go through <see cref="IAtRestSecretProtector"/> (idempotent and read-both, both wrong for a key), and <see cref="PurposeTag"/> binds it to this
/// purpose so an envelope written for anything else under the same master key never unwraps as a data-stream key.
/// </summary>
public sealed class DataStreamKeyProtector(Lazy<IVariableEncryptionService> encryption) : IDataStreamKeyProtector, ISingletonDependency
{
    /// <summary>Scope passed to the encryption service (log context / legacy KDF), not a domain separator. Persisted format: pinned.</summary>
    public const int KdfScope = -1003;

    /// <summary>Prepended to the key inside the envelope; an unwrapped plaintext without it is refused. Persisted format: pinned.</summary>
    public const string PurposeTag = "squid.halibut-data-stream-key.v1\n";

    public byte[] NewKey() => RandomNumberGenerator.GetBytes(DataStreamCodec.KeyBytes);

    public string Wrap(byte[] key) => encryption.Value.EncryptAsync(PurposeTag + Convert.ToBase64String(key), KdfScope);

    public byte[] Unwrap(string wrapped, Guid streamId)
    {
        var plaintext = Decrypt(wrapped, streamId);
        var key = new byte[DataStreamCodec.KeyBytes];

        if (!plaintext.StartsWith(PurposeTag, StringComparison.Ordinal) || !Convert.TryFromBase64String(plaintext[PurposeTag.Length..], key, out var written) || written != key.Length)
            throw new DataStreamUnreadableException(streamId, "its key envelope is not a data-stream key");

        return key;
    }

    private string Decrypt(string wrapped, Guid streamId)
    {
        if (string.IsNullOrEmpty(wrapped) || !wrapped.StartsWith(VariableEncryptionService.EncryptionPrefixV2, StringComparison.Ordinal))
            throw new DataStreamUnreadableException(streamId, "its key envelope is not encrypted");

        try
        {
            return encryption.Value.Decrypt(wrapped, KdfScope);
        }
        catch (InvalidOperationException ex)
        {
            throw new DataStreamUnreadableException(streamId, "its key cannot be decrypted with the master key", ex);
        }
    }
}
