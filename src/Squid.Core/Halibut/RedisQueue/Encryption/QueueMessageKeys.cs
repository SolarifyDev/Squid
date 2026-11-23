using System.Collections.Concurrent;
using System.Security.Cryptography;
using Squid.Core.Services.Security;

namespace Squid.Core.Halibut.RedisQueue.Encryption;

public interface IQueueMessageKeys
{
    /// <summary>The key new messages are sealed with, and its wrapped form to store beside them.</summary>
    (byte[] Key, string Wrapped) Current();

    /// <summary>The key a stored message names, or throws <see cref="QueueMessageUnreadableException"/> when it cannot be unwrapped (rotated master key, tampering).</summary>
    byte[] Resolve(string wrapped);
}

public interface IQueueMessageKeyProtector
{
    /// <summary>The key sealed with the variable-encryption master key. Throws when the master key is unavailable: nothing is ever queued unprotected.</summary>
    string Wrap(byte[] key);

    byte[] Unwrap(string wrapped);
}

/// <summary>
/// Seals the queue's data keys with the master key through <see cref="IVariableEncryptionService"/> (the V2 envelope every at-rest secret uses). Like the data-stream
/// key it is bound to its own purpose by <see cref="PurposeTag"/> and <see cref="KdfScope"/>, so an envelope written for anything else under the same master key
/// never unwraps as a queue key.
/// </summary>
public sealed class QueueMessageKeyProtector(Lazy<IVariableEncryptionService> encryption) : IQueueMessageKeyProtector
{
    /// <summary>Scope passed to the encryption service (log context / legacy KDF), not a domain separator. Persisted format: pinned.</summary>
    public const int KdfScope = -1004;

    /// <summary>Prepended to the key inside the envelope; an unwrapped plaintext without it is refused. Persisted format: pinned.</summary>
    public const string PurposeTag = "squid.halibut-queue-message-key.v1\n";

    public string Wrap(byte[] key) => encryption.Value.EncryptAsync(PurposeTag + Convert.ToBase64String(key), KdfScope);

    public byte[] Unwrap(string wrapped)
    {
        var plaintext = Decrypt(wrapped);
        var key = new byte[QueueMessageEnvelope.KeyBytes];

        if (!plaintext.StartsWith(PurposeTag, StringComparison.Ordinal) || !Convert.TryFromBase64String(plaintext[PurposeTag.Length..], key, out var written) || written != key.Length)
            throw new QueueMessageUnreadableException("its key envelope is not a queue message key");

        return key;
    }

    private string Decrypt(string wrapped)
    {
        if (string.IsNullOrEmpty(wrapped) || !wrapped.StartsWith(VariableEncryptionService.EncryptionPrefixV2, StringComparison.Ordinal))
            throw new QueueMessageUnreadableException("its key envelope is not encrypted");

        try
        {
            return encryption.Value.Decrypt(wrapped, KdfScope);
        }
        catch (InvalidOperationException ex)
        {
            throw new QueueMessageUnreadableException("its key cannot be decrypted with the master key", ex);
        }
    }
}

/// <summary>
/// One data key per rotation period, not per message. Wrapping or unwrapping a key is a 600 000-iteration PBKDF2 (hundreds of milliseconds), and a deployment
/// sends a status request every second, so a key per message would cost far more than the work it protects. Messages stay individually sealed (a fresh random
/// nonce each, far below AES-GCM's message limit for a key used for minutes), the wrapped key travels with each, and a pod that unwraps a key remembers it for
/// the rest of its life as a key, so each key costs one PBKDF2 per pod.
/// </summary>
public sealed class QueueMessageKeys(IQueueMessageKeyProtector protector, RedisQueueOptions options, TimeProvider clock) : IQueueMessageKeys
{
    private const int MaxRememberedKeys = 64;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte[]> _unwrapped = new();
    private (byte[] Key, string Wrapped) _current;
    private long _createdAt;

    public (byte[] Key, string Wrapped) Current()
    {
        lock (_gate)
        {
            if (_current.Key == null || clock.GetElapsedTime(_createdAt) >= options.KeyRotation)
                Rotate();

            return _current;
        }
    }

    public byte[] Resolve(string wrapped)
    {
        if (_unwrapped.TryGetValue(wrapped, out var known)) return known;

        var key = protector.Unwrap(wrapped);

        Remember(wrapped, key);

        return key;
    }

    private void Rotate()
    {
        var key = RandomNumberGenerator.GetBytes(QueueMessageEnvelope.KeyBytes);
        var wrapped = protector.Wrap(key);

        _current = (key, wrapped);
        _createdAt = clock.GetTimestamp();

        Remember(wrapped, key);
    }

    // A message lives at most about twenty minutes in Redis, so a key older than a few rotations is never asked for again; a miss only costs one more unwrap.
    private void Remember(string wrapped, byte[] key)
    {
        if (_unwrapped.Count >= MaxRememberedKeys) _unwrapped.Clear();

        _unwrapped[wrapped] = key;
    }
}
