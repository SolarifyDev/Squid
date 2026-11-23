using System.IO;
using System.Linq;
using Halibut;
using Squid.Core.Halibut.DataStreamStorage;
using Squid.Core.Halibut.DataStreamStorage.Exceptions;
using Squid.Core.Services.Security;
using static Squid.UnitTests.Halibut.DataStreamStorage.DataStreamStorageTestSupport;

namespace Squid.UnitTests.Halibut.DataStreamStorage;

public class DataStreamCodecTests
{
    private static readonly Guid StreamId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static byte[] Seal(byte[] plain, int index, Guid? streamId = null)
    {
        using var sealer = new DataStreamChunkSealer(Key, streamId ?? StreamId, 64);

        return sealer.Seal(plain, index).ToArray();
    }

    private static int Open(byte[] stored, int index, Guid? streamId = null) => DataStreamCodec.Open(stored, Key, streamId ?? StreamId, index, new byte[64]);

    // Persisted format: a change here orphans every stored stream of a rolling upgrade.
    [Fact]
    public void PersistedFormat_IsPinned()
    {
        DataStreamCodec.KeyBytes.ShouldBe(32);
        DataStreamCodec.NonceBytes.ShouldBe(12);
        DataStreamCodec.TagBytes.ShouldBe(16);
        DataStreamCodec.Overhead.ShouldBe(28);
        System.Text.Encoding.UTF8.GetString(DataStreamCodec.AssociatedData(StreamId, 3)).ShouldBe("11111111222233334444555555555555|3");
        System.Text.Encoding.UTF8.GetString(DataStreamCodec.AssociatedData(StreamId, 3, isFinal: true)).ShouldBe("11111111222233334444555555555555|3|final");
        System.Text.Encoding.UTF8.GetString(DataStreamCodec.AssociatedData(StreamId, 3, isFinal: false)).ShouldBe("11111111222233334444555555555555|3");
    }

    [Fact]
    public void ALastChunk_OnlyOpensAsTheLastChunk_AndAMiddleChunkNeverDoes()
    {
        using var sealer = new DataStreamChunkSealer(Key, StreamId, 64);
        var last = sealer.Seal(Bytes(40), 2, isFinal: true).ToArray();
        var middle = sealer.Seal(Bytes(40), 2, isFinal: false).ToArray();

        DataStreamCodec.Open(last, Key, StreamId, 2, new byte[64], isFinal: true).ShouldBe(40);
        DataStreamCodec.Open(middle, Key, StreamId, 2, new byte[64], isFinal: false).ShouldBe(40);

        Should.Throw<DataStreamUnreadableException>(() => DataStreamCodec.Open(last, Key, StreamId, 2, new byte[64], isFinal: false));
        Should.Throw<DataStreamUnreadableException>(() => DataStreamCodec.Open(middle, Key, StreamId, 2, new byte[64], isFinal: true));
    }

    [Fact]
    public void SealThenOpen_RoundTrips_AndAddsOnlyNonceAndTag()
    {
        var plain = Bytes(40);
        var sealedChunk = Seal(plain, 0);

        sealedChunk.Length.ShouldBe(40 + DataStreamCodec.Overhead);

        var destination = new byte[64];
        DataStreamCodec.Open(sealedChunk, Key, StreamId, 0, destination).ShouldBe(40);
        destination.Take(40).ToArray().ShouldBe(plain);
    }

    [Fact]
    public void Open_ChunkMovedToAnotherIndex_FailsAuthentication()
        => Should.Throw<DataStreamUnreadableException>(() => Open(Seal(Bytes(40), 0), 1));

    [Fact]
    public void Open_ChunkMovedToAnotherStream_FailsAuthentication()
        => Should.Throw<DataStreamUnreadableException>(() => Open(Seal(Bytes(40), 0), 0, Guid.NewGuid()));

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(27)]
    [InlineData(35)]
    public void Open_AnyChangedByte_FailsAuthentication(int position)
    {
        var sealedChunk = Seal(Bytes(40), 0);

        sealedChunk[position] ^= 1;

        Should.Throw<DataStreamUnreadableException>(() => Open(sealedChunk, 0));
    }

    [Fact]
    public void Open_TooShortToBeSealed_IsUnreadable()
        => Should.Throw<DataStreamUnreadableException>(() => Open(new byte[DataStreamCodec.Overhead - 1], 0));

    [Fact]
    public void Open_WrongKey_FailsAuthentication()
    {
        var sealedChunk = Seal(Bytes(40), 0);

        Should.Throw<DataStreamUnreadableException>(() => DataStreamCodec.Open(sealedChunk, new byte[32], StreamId, 0, new byte[64]));
    }

    [Fact]
    public void Seal_SameChunkTwice_UsesAFreshNonce()
    {
        using var sealer = new DataStreamChunkSealer(Key, StreamId, 64);
        var plain = Bytes(40);

        var first = sealer.Seal(plain, 0).ToArray();
        var second = sealer.Seal(plain, 0).ToArray();

        first.Take(DataStreamCodec.NonceBytes).ShouldNotBe(second.Take(DataStreamCodec.NonceBytes));
    }
}

public class DataStreamReadStreamTests
{
    private const int Chunk = 16;

    private static async Task<(InMemoryDataStreamPersistence Persistence, StoredDataStreamHeader Header, byte[] Key, byte[] Payload)> StoredAsync(int length = 50)
    {
        var persistence = new InMemoryDataStreamPersistence();
        var keys = Keys();
        var payload = Bytes(length);
        var stream = DataStream.FromBytes(payload);

        await Store(persistence, Chunk, keys).StoreDataStreams([stream], false, CancellationToken.None);

        var header = persistence.Streams[stream.Id].Header;

        return (persistence, header, keys.Unwrap(header.ProtectedKey, stream.Id), payload);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var copy = new MemoryStream();

        await stream.CopyToAsync(copy);

        return copy.ToArray();
    }

    [Fact]
    public async Task LengthAndSeekToStart_AreWhatHalibutsCopierNeeds()
    {
        var (persistence, header, key, payload) = await StoredAsync();
        using var stream = new DataStreamReadStream(persistence, header, key);

        stream.Length.ShouldBe(payload.Length);
        stream.Seek(0, SeekOrigin.Begin).ShouldBe(0);
        (await ReadAllAsync(stream)).ShouldBe(payload);
    }

    [Fact]
    public async Task SeekToStart_AfterReading_RereadsFromTheBeginning()
    {
        var (persistence, header, key, payload) = await StoredAsync();
        using var stream = new DataStreamReadStream(persistence, header, key);

        await ReadAllAsync(stream);
        stream.Position = 0;

        (await ReadAllAsync(stream)).ShouldBe(payload);
    }

    [Fact]
    public async Task Seek_ToAnyOtherPlace_IsNotSupported()
    {
        var (persistence, header, key, _) = await StoredAsync();
        using var stream = new DataStreamReadStream(persistence, header, key);

        Should.Throw<NotSupportedException>(() => stream.Seek(5, SeekOrigin.Begin));
        Should.Throw<NotSupportedException>(() => stream.Seek(0, SeekOrigin.End));
    }

    [Fact]
    public async Task Read_WithTinyBuffers_StillYieldsEveryByteOnce()
    {
        var (persistence, header, key, payload) = await StoredAsync();
        using var stream = new DataStreamReadStream(persistence, header, key);
        var read = new List<byte>();
        var one = new byte[3];
        int count;

        while ((count = await stream.ReadAsync(one)) > 0) read.AddRange(one.Take(count));

        read.ToArray().ShouldBe(payload);
    }

    [Fact]
    public async Task Read_MissingChunk_ThrowsUnreadable_NeverShortData()
    {
        var (persistence, header, key, _) = await StoredAsync();
        persistence.Streams[header.StreamId].Chunks.Remove(1);
        using var stream = new DataStreamReadStream(persistence, header, key);

        await Should.ThrowAsync<DataStreamUnreadableException>(() => ReadAllAsync(stream));
    }

    [Fact]
    public async Task Read_ChunksSwappedInStorage_ThrowsUnreadable()
    {
        var (persistence, header, key, _) = await StoredAsync();
        var chunks = persistence.Streams[header.StreamId].Chunks;
        (chunks[0], chunks[1]) = (chunks[1], chunks[0]);
        using var stream = new DataStreamReadStream(persistence, header, key);

        await Should.ThrowAsync<DataStreamUnreadableException>(() => ReadAllAsync(stream));
    }

    [Fact]
    public async Task Read_FewerChunksThanTheHeaderLength_ThrowsUnreadable()
    {
        var (persistence, header, key, _) = await StoredAsync();
        using var stream = new DataStreamReadStream(persistence, header with { ChunkCount = header.ChunkCount - 1 }, key);

        await Should.ThrowAsync<DataStreamUnreadableException>(() => ReadAllAsync(stream));
    }

    [Fact]
    public async Task Read_MoreBytesThanTheHeaderLength_ThrowsUnreadable()
    {
        var (persistence, header, key, _) = await StoredAsync();
        using var stream = new DataStreamReadStream(persistence, header with { Length = header.Length - 1 }, key);

        await Should.ThrowAsync<DataStreamUnreadableException>(() => ReadAllAsync(stream));
    }

    [Fact]
    public async Task Read_EmptyStream_IsImmediatelyAtTheEnd()
    {
        var (persistence, header, key, _) = await StoredAsync(0);
        using var stream = new DataStreamReadStream(persistence, header, key);

        stream.Length.ShouldBe(0);
        (await ReadAllAsync(stream)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SyncRead_Works_ForCallersThatAreNotAsync()
    {
        var (persistence, header, key, payload) = await StoredAsync();
        using var stream = new DataStreamReadStream(persistence, header, key);
        using var copy = new MemoryStream();

        stream.CopyTo(copy);

        copy.ToArray().ShouldBe(payload);
    }
}

public class DataStreamChunkWriterTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(16, 2)]
    [InlineData(17, 3)]
    public async Task Writes_InAnyPieceSize_CutAtTheChunkSize_WithNoEmptyTrailingChunk(int length, int expectedChunks)
    {
        var persistence = new InMemoryDataStreamPersistence();
        var key = new byte[32];
        var id = Guid.NewGuid();
        await using var batch = await persistence.BeginBatchAsync(CancellationToken.None);
        await batch.BeginStreamAsync(id, "wrapped", CancellationToken.None);

        await using var writer = new DataStreamChunkWriter(batch, id, key, 8);
        var payload = Bytes(length);

        for (var offset = 0; offset < length; offset += 5)
            await writer.WriteAsync(payload.AsMemory(offset, Math.Min(5, length - offset)));

        await writer.CompleteAsync(CancellationToken.None);
        await batch.CommitAsync(CancellationToken.None);

        var stored = persistence.Streams[id];
        stored.Header.Length.ShouldBe(length);
        stored.Header.ChunkCount.ShouldBe(expectedChunks);
        stored.Chunks.Keys.OrderBy(k => k).ShouldBe(Enumerable.Range(0, expectedChunks));
        stored.Chunks.Values.Sum(c => c.Length - DataStreamCodec.Overhead).ShouldBe(length);
        stored.Chunks.Values.Take(Math.Max(0, expectedChunks - 1)).ShouldAllBe(c => c.Length == 8 + DataStreamCodec.Overhead, "every chunk but the last is full");
    }

    [Fact]
    public async Task SyncWrite_Works_ForWritersThatAreNotAsync()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var id = Guid.NewGuid();
        await using var batch = await persistence.BeginBatchAsync(CancellationToken.None);
        await batch.BeginStreamAsync(id, "wrapped", CancellationToken.None);
        await using var writer = new DataStreamChunkWriter(batch, id, new byte[32], 8);

        writer.Write(Bytes(20), 0, 20);
        await writer.CompleteAsync(CancellationToken.None);
        await batch.CommitAsync(CancellationToken.None);

        persistence.Streams[id].Header.Length.ShouldBe(20);
    }
}
