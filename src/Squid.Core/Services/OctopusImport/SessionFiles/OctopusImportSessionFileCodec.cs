using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.SessionFiles;

/// <summary>
/// How one chunk of an import session file is stored: optionally deflate-compressed, then, with a session key, AES-256-GCM sealed as
/// <c>nonce | tag | ciphertext</c>. The associated data names the session, the path and the chunk index, so a chunk moved to another
/// index, path or session fails authentication instead of being read as that one. Every constant here is a persisted format (pinned by tests).
/// </summary>
public static class OctopusImportSessionFileCodec
{
    public const int ChunkBytes = 1024 * 1024;
    public const int KeyBytes = 32;
    public const string AesGcmV1 = "aes-gcm-v1";
    public const string None = "none";
    public const string Identity = "identity";
    public const string Deflate = "deflate";

    internal const int NonceBytes = 12;
    internal const int TagBytes = 16;

    public static string EncodingName(OctopusImportSessionFileEncoding encoding) => encoding == OctopusImportSessionFileEncoding.Deflate ? Deflate : Identity;

    public static string EncryptionName(byte[] key) => key == null ? None : AesGcmV1;

    public static byte[] AssociatedData(Guid sessionId, string path, int chunkIndex) => Encoding.UTF8.GetBytes($"{sessionId:N}|{path}|{chunkIndex}");

    /// <summary>The chunk's original bytes. Throws <see cref="CryptographicException"/> when the chunk was not sealed for this session, path and index, or was changed.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> stored, byte[] key, string encryption, string encoding, Guid sessionId, string path, int chunkIndex)
    {
        var body = Open(stored, key, encryption, AssociatedData(sessionId, path, chunkIndex));

        return encoding == Deflate ? Inflate(body) : body;
    }

    private static byte[] Open(ReadOnlySpan<byte> stored, byte[] key, string encryption, byte[] associatedData)
    {
        if (encryption == None) return stored.ToArray();

        if (encryption != AesGcmV1 || key == null || stored.Length < NonceBytes + TagBytes)
            throw new CryptographicException("The import session file cannot be decrypted with the session's key.");

        var plain = new byte[stored.Length - NonceBytes - TagBytes];

        using var aes = new AesGcm(key, TagBytes);

        aes.Decrypt(stored[..NonceBytes], stored[(NonceBytes + TagBytes)..], stored.Slice(NonceBytes, TagBytes), plain, associatedData);

        return plain;
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        inflate.CopyTo(output);

        return output.ToArray();
    }
}

/// <summary>
/// Seals the chunks of one file. The ciphertext buffer is reused between chunks, so a large file is encoded without one allocation per
/// chunk; what <see cref="Encode"/> returns is valid only until the next call.
/// </summary>
public sealed class OctopusImportSessionFileChunkEncoder(Guid sessionId, string path, byte[] key, OctopusImportSessionFileEncoding encoding) : IDisposable
{
    private readonly AesGcm _aes = key == null ? null : new AesGcm(key, OctopusImportSessionFileCodec.TagBytes);

    private byte[] _sealed = [];

    public ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> plain, int chunkIndex)
    {
        var body = encoding == OctopusImportSessionFileEncoding.Deflate ? Compress(plain) : plain;

        return _aes == null ? body : Seal(body, chunkIndex);
    }

    private static ReadOnlyMemory<byte> Compress(ReadOnlyMemory<byte> plain)
    {
        using var output = new MemoryStream();

        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(plain.Span);

        return output.ToArray();
    }

    private ReadOnlyMemory<byte> Seal(ReadOnlyMemory<byte> body, int chunkIndex)
    {
        const int header = OctopusImportSessionFileCodec.NonceBytes + OctopusImportSessionFileCodec.TagBytes;

        if (_sealed.Length < header + body.Length) _sealed = new byte[Math.Max(header + body.Length, OctopusImportSessionFileCodec.ChunkBytes + header)];

        var nonce = _sealed.AsSpan(0, OctopusImportSessionFileCodec.NonceBytes);

        RandomNumberGenerator.Fill(nonce);

        _aes.Encrypt(nonce, body.Span, _sealed.AsSpan(header, body.Length), _sealed.AsSpan(OctopusImportSessionFileCodec.NonceBytes, OctopusImportSessionFileCodec.TagBytes), OctopusImportSessionFileCodec.AssociatedData(sessionId, path, chunkIndex));

        return _sealed.AsMemory(0, header + body.Length);
    }

    public void Dispose() => _aes?.Dispose();
}

/// <summary>
/// Cuts a stream into <see cref="OctopusImportSessionFileCodec.ChunkBytes"/> chunks, hashing the original bytes on the way, so a file of
/// any size is stored holding one chunk in memory. <see cref="Completed"/> turns true once the stream ended; only then are
/// <see cref="Length"/> and <see cref="Sha256"/> final.
/// </summary>
public sealed class OctopusImportSessionFileChunker(Stream content, OctopusImportSessionFileChunkEncoder encoder) : IDisposable
{
    private readonly byte[] _buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(OctopusImportSessionFileCodec.ChunkBytes);
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private string _sha256;

    public int Count { get; private set; }

    public long Length { get; private set; }

    public bool Completed { get; private set; }

    public string Sha256 => Completed ? _sha256 ??= Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant() : null;

    /// <summary>The next stored chunk; null once the stream has no more bytes.</summary>
    public async Task<ReadOnlyMemory<byte>?> NextAsync(CancellationToken ct)
    {
        var filled = await FillAsync(ct).ConfigureAwait(false);

        Completed = filled < OctopusImportSessionFileCodec.ChunkBytes;

        if (filled == 0) return null;

        _hash.AppendData(_buffer, 0, filled);
        Length += filled;

        return encoder.Encode(_buffer.AsMemory(0, filled), Count++);
    }

    private async Task<int> FillAsync(CancellationToken ct)
    {
        var filled = 0;

        while (filled < OctopusImportSessionFileCodec.ChunkBytes)
        {
            var read = await content.ReadAsync(_buffer.AsMemory(filled, OctopusImportSessionFileCodec.ChunkBytes - filled), ct).ConfigureAwait(false);

            if (read == 0) break;

            filled += read;
        }

        return filled;
    }

    public void Dispose()
    {
        System.Buffers.ArrayPool<byte>.Shared.Return(_buffer);
        _hash.Dispose();
    }
}
