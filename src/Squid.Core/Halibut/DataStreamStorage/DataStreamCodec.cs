using System.Security.Cryptography;
using System.Text;
using Squid.Core.Halibut.DataStreamStorage.Exceptions;

namespace Squid.Core.Halibut.DataStreamStorage;

/// <summary>
/// How one chunk of a data stream is stored: AES-256-GCM sealed as <c>nonce | tag | ciphertext</c>. The associated data names the stream and the chunk
/// index, so a chunk moved to another index or stream fails authentication instead of being read as that one. Every constant is a persisted format (pinned by tests).
/// </summary>
public static class DataStreamCodec
{
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int Overhead = NonceBytes + TagBytes;

    public static byte[] AssociatedData(Guid streamId, int chunkIndex) => Encoding.UTF8.GetBytes($"{streamId:N}|{chunkIndex}");

    /// <summary>Opens a sealed chunk into <paramref name="destination"/> and returns its plain length. Throws <see cref="DataStreamUnreadableException"/> when it was not sealed for this stream and index, or was changed.</summary>
    public static int Open(ReadOnlySpan<byte> stored, byte[] key, Guid streamId, int chunkIndex, Span<byte> destination)
    {
        if (stored.Length < Overhead || destination.Length < stored.Length - Overhead)
            throw new DataStreamUnreadableException(streamId, $"chunk {chunkIndex} has an invalid size");

        try
        {
            using var aes = new AesGcm(key, TagBytes);

            aes.Decrypt(stored[..NonceBytes], stored[Overhead..], stored.Slice(NonceBytes, TagBytes), destination[..(stored.Length - Overhead)], AssociatedData(streamId, chunkIndex));

            return stored.Length - Overhead;
        }
        catch (CryptographicException ex)
        {
            throw new DataStreamUnreadableException(streamId, $"chunk {chunkIndex} failed authentication", ex);
        }
    }
}

/// <summary>Seals the chunks of one stream. The output buffer is reused, so what <see cref="Seal"/> returns is valid only until the next call.</summary>
public sealed class DataStreamChunkSealer(byte[] key, Guid streamId, int maxPlainBytes) : IDisposable
{
    private readonly AesGcm _aes = new(key, DataStreamCodec.TagBytes);
    private readonly byte[] _sealed = new byte[DataStreamCodec.Overhead + maxPlainBytes];

    public ReadOnlyMemory<byte> Seal(ReadOnlyMemory<byte> plain, int chunkIndex)
    {
        var nonce = _sealed.AsSpan(0, DataStreamCodec.NonceBytes);

        RandomNumberGenerator.Fill(nonce);

        _aes.Encrypt(nonce, plain.Span, _sealed.AsSpan(DataStreamCodec.Overhead, plain.Length), _sealed.AsSpan(DataStreamCodec.NonceBytes, DataStreamCodec.TagBytes), DataStreamCodec.AssociatedData(streamId, chunkIndex));

        return _sealed.AsMemory(0, DataStreamCodec.Overhead + plain.Length);
    }

    public void Dispose() => _aes.Dispose();
}
