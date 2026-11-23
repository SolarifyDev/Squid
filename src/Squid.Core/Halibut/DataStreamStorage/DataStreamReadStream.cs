using Squid.Core.Halibut.DataStreamStorage.Exceptions;

namespace Squid.Core.Halibut.DataStreamStorage;

/// <summary>
/// A stored data stream read back chunk by chunk: each chunk is fetched with its own short query when the previous one is used up, so no database connection
/// is held while a slow consumer (the agent's network) drains the bytes, and only one chunk is ever in memory. Every chunk must be there, authentic and in
/// order, the last one must be sealed as the last (so a lowered chunk count cannot hide a missing tail), and the bytes must add up to the header's length; anything else throws <see cref="DataStreamUnreadableException"/> rather than yielding short or wrong data.
/// Halibut's copier asks for <see cref="Length"/> and seeks to the start before reading, so both are supported; seeking anywhere else is not.
/// </summary>
public sealed class DataStreamReadStream(IDataStreamPersistence persistence, StoredDataStreamHeader header, byte[] key) : Stream
{
    private byte[] _plain = [];
    private int _plainLength;
    private int _plainOffset;
    private int _nextChunk;
    private long _position;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;

        while (_plainOffset == _plainLength)
            if (!await LoadNextChunkAsync(cancellationToken).ConfigureAwait(false)) return 0;

        var count = Math.Min(buffer.Length, _plainLength - _plainOffset);

        _plain.AsMemory(_plainOffset, count).CopyTo(buffer);

        _plainOffset += count;
        _position += count;

        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    // False at the end of the stream; the end is only believed when the chunks seen add up to the header.
    private async Task<bool> LoadNextChunkAsync(CancellationToken ct)
    {
        if (header.ChunkCount < 1)
            throw new DataStreamUnreadableException(header.StreamId, "it has no chunks, and every stream has at least its last one");

        if (_nextChunk >= header.ChunkCount) return EnsureComplete();

        var stored = await persistence.ReadChunkAsync(header.StreamId, _nextChunk, ct).ConfigureAwait(false)
            ?? throw new DataStreamUnreadableException(header.StreamId, $"chunk {_nextChunk} is missing");

        if (_plain.Length < stored.Length - DataStreamCodec.Overhead) _plain = new byte[stored.Length - DataStreamCodec.Overhead];

        _plainLength = DataStreamCodec.Open(stored, key, header.StreamId, _nextChunk, _plain, isFinal: _nextChunk == header.ChunkCount - 1);
        _plainOffset = 0;
        _nextChunk++;

        if (_position + _plainLength > header.Length)
            throw new DataStreamUnreadableException(header.StreamId, "its chunks hold more bytes than its length");

        return true;
    }

    private bool EnsureComplete()
    {
        if (_position != header.Length)
            throw new DataStreamUnreadableException(header.StreamId, $"its chunks hold {_position} bytes, not the {header.Length} its length says");

        return false;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (origin != SeekOrigin.Begin || offset != 0)
            throw new NotSupportedException("A stored data stream can only be rewound to its start.");

        _nextChunk = 0;
        _plainLength = 0;
        _plainOffset = 0;
        _position = 0;

        return 0;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => header.Length;

    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    public override void Flush() { }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
