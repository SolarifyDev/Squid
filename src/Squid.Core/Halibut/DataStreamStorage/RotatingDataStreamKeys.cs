using System.Collections.Concurrent;

namespace Squid.Core.Halibut.DataStreamStorage;

/// <summary>
/// One data key per rotation period, not per message, and each unwrapped key remembered. Wrapping or unwrapping a key is a 600 000-iteration PBKDF2 (hundreds of
/// milliseconds) and a deployment sends several requests with data streams, so a key per message would cost that on both pods for every one of them. Streams stay
/// individually sealed (a fresh random nonce per chunk, far below AES-GCM's limit for a key used for minutes), and the wrapped key is stored beside each stream, so
/// any pod can still open it. The same approach as the queue messages' keys.
/// </summary>
public sealed class RotatingDataStreamKeys(IDataStreamKeyProtector protector, TimeSpan rotation, TimeProvider clock)
{
    private const int MaxRememberedKeys = 64;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, byte[]> _unwrapped = new();
    private (byte[] Key, string Wrapped) _current;
    private long _createdAt;

    /// <summary>The key new streams are sealed with, and its wrapped form to store beside them.</summary>
    public (byte[] Key, string Wrapped) Current()
    {
        lock (_gate)
        {
            if (_current.Key == null || clock.GetElapsedTime(_createdAt) >= rotation)
                Rotate();

            return _current;
        }
    }

    /// <summary>The key a stored stream names. A failure to unwrap (rotated master key, tampering) throws and is never remembered.</summary>
    public byte[] Resolve(string wrapped, Guid streamId)
    {
        if (_unwrapped.TryGetValue(wrapped, out var known)) return known;

        var key = protector.Unwrap(wrapped, streamId);

        Remember(wrapped, key);

        return key;
    }

    private void Rotate()
    {
        var key = protector.NewKey();
        var wrapped = protector.Wrap(key);

        _current = (key, wrapped);
        _createdAt = clock.GetTimestamp();

        Remember(wrapped, key);
    }

    // A stream lives at most about an hour, so a key older than a few rotations is never asked for again; a miss only costs one more unwrap.
    private void Remember(string wrapped, byte[] key)
    {
        if (_unwrapped.Count >= MaxRememberedKeys) _unwrapped.Clear();

        _unwrapped[wrapped] = key;
    }
}
