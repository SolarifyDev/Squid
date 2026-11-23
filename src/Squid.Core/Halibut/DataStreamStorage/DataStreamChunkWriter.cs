namespace Squid.Core.Halibut.DataStreamStorage;

/// <summary>
/// The destination a data stream is written into: bytes are cut into chunks of <c>chunkBytes</c>, each sealed and handed to the batch as soon as it is full,
/// so a stream of any size is stored holding one plain chunk and one sealed chunk in memory. A full chunk is only sealed once more data follows it or the stream
/// completes, because the last chunk is sealed as the last one (see <see cref="DataStreamCodec"/>): a stream whose length is an exact multiple of the chunk size
/// ends on a full chunk and gets no empty trailing one, and an empty stream is one empty last chunk. <see cref="CompleteAsync"/> writes the last chunk and the final length.
/// </summary>
public sealed class DataStreamChunkWriter : Stream
{
    private readonly IDataStreamBatchWriter _batch;
    private readonly Guid _streamId;
    private readonly DataStreamChunkSealer _sealer;
    private readonly byte[] _plain;

    private int _filled;
    private int _chunkCount;
    private long _length;

    public DataStreamChunkWriter(IDataStreamBatchWriter batch, Guid streamId, byte[] key, int chunkBytes)
    {
        _batch = batch;
        _streamId = streamId;
        _plain = new byte[chunkBytes];
        _sealer = new DataStreamChunkSealer(key, streamId, chunkBytes);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (buffer.Length > 0)
        {
            if (_filled == _plain.Length) await FlushChunkAsync(isFinal: false, cancellationToken).ConfigureAwait(false);

            var taken = Math.Min(buffer.Length, _plain.Length - _filled);

            buffer[..taken].CopyTo(_plain.AsMemory(_filled));

            _filled += taken;
            _length += taken;
            buffer = buffer[taken..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <summary>Writes the last chunk and the stream's final length and chunk count.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        await FlushChunkAsync(isFinal: true, ct).ConfigureAwait(false);

        await _batch.CompleteStreamAsync(_streamId, _length, _chunkCount, ct).ConfigureAwait(false);
    }

    private async Task FlushChunkAsync(bool isFinal, CancellationToken ct)
    {
        var sealedChunk = _sealer.Seal(_plain.AsMemory(0, _filled), _chunkCount, isFinal);

        await _batch.AddChunkAsync(_streamId, _chunkCount, sealedChunk, ct).ConfigureAwait(false);

        _chunkCount++;
        _filled = 0;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position { get => _length; set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _sealer.Dispose();

        base.Dispose(disposing);
    }
}
