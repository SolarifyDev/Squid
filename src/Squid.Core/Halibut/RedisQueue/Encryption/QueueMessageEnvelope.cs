using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Squid.Core.Halibut.RedisQueue.Encryption;

/// <summary>A queued message that cannot be opened: not one of ours, changed after sealing, or sealed under a key this pod cannot unwrap. Never retried, and not Redis's fault.</summary>
public sealed class QueueMessageUnreadableException(string reason, Exception inner = null) : InvalidOperationException($"A queued Halibut message cannot be read: {reason}.", inner);

/// <summary>
/// How a queued Halibut message (a request or a response, BSON bytes) is stored in Redis: <c>magic | wrapped-key length | wrapped key | nonce | tag | ciphertext</c>,
/// AES-256-GCM, with the magic and the wrapped key as associated data, so a message cannot be moved under another key envelope. The wrapped key is the data key
/// sealed with the master key, so any pod can open a message and nothing in Redis can without it. Every constant is a persisted format (pinned by tests).
/// </summary>
public static class QueueMessageEnvelope
{
    public const string Magic = "SQQ1";
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    public static byte[] Seal(ReadOnlySpan<byte> plain, byte[] key, string wrappedKey)
    {
        var wrapped = Encoding.UTF8.GetBytes(wrappedKey);
        var header = Header(wrapped);
        var sealedMessage = new byte[header.Length + NonceBytes + TagBytes + plain.Length];

        header.CopyTo(sealedMessage, 0);

        var nonce = sealedMessage.AsSpan(header.Length, NonceBytes);
        var tag = sealedMessage.AsSpan(header.Length + NonceBytes, TagBytes);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagBytes);

        aes.Encrypt(nonce, plain, sealedMessage.AsSpan(header.Length + NonceBytes + TagBytes), tag, header);

        return sealedMessage;
    }

    /// <summary>The wrapped key a sealed message names, so the caller can unwrap it before <see cref="Open"/>.</summary>
    public static string WrappedKeyOf(ReadOnlySpan<byte> sealedMessage)
    {
        if (sealedMessage.Length < Magic.Length + sizeof(int) || !sealedMessage[..Magic.Length].SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
            throw new QueueMessageUnreadableException("it is not a sealed queue message");

        var length = BinaryPrimitives.ReadInt32BigEndian(sealedMessage.Slice(Magic.Length, sizeof(int)));

        if (length <= 0 || sealedMessage.Length < Magic.Length + sizeof(int) + length + NonceBytes + TagBytes)
            throw new QueueMessageUnreadableException("its key envelope is truncated");

        return Encoding.UTF8.GetString(sealedMessage.Slice(Magic.Length + sizeof(int), length));
    }

    public static byte[] Open(ReadOnlySpan<byte> sealedMessage, byte[] key)
    {
        var wrapped = Encoding.UTF8.GetBytes(WrappedKeyOf(sealedMessage));
        var headerLength = Magic.Length + sizeof(int) + wrapped.Length;
        var plain = new byte[sealedMessage.Length - headerLength - NonceBytes - TagBytes];

        try
        {
            using var aes = new AesGcm(key, TagBytes);

            aes.Decrypt(sealedMessage.Slice(headerLength, NonceBytes), sealedMessage[(headerLength + NonceBytes + TagBytes)..], sealedMessage.Slice(headerLength + NonceBytes, TagBytes), plain, sealedMessage[..headerLength]);

            return plain;
        }
        catch (CryptographicException ex)
        {
            throw new QueueMessageUnreadableException("it failed authentication (changed, or sealed under another key)", ex);
        }
    }

    private static byte[] Header(byte[] wrapped)
    {
        var header = new byte[Magic.Length + sizeof(int) + wrapped.Length];

        Encoding.ASCII.GetBytes(Magic, header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(Magic.Length, sizeof(int)), wrapped.Length);
        wrapped.CopyTo(header, Magic.Length + sizeof(int));

        return header;
    }
}
