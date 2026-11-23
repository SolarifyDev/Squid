using System.IO;
using System.Linq;
using System.Text;
using Halibut;
using Halibut.Queue.Redis.MessageStorage;
using Squid.Core.Halibut.DataStreamStorage;
using Squid.Core.Halibut.DataStreamStorage.Exceptions;
using Squid.Core.Services.Security;
using static Squid.UnitTests.Halibut.DataStreamStorage.DataStreamStorageTestSupport;

namespace Squid.UnitTests.Halibut.DataStreamStorage;

/// <summary>
/// Unit tier: the contract Halibut's Redis queue relies on (store before queueing, rehydrate on the collecting pod, single read), run against a persistence
/// that models the database's all-or-nothing batch. Chunks are 16 bytes so every boundary is a handful of bytes. The real database is covered by the integration tier.
/// </summary>
public class DistributedDataStreamStoreTests
{
    private const int Chunk = 16;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(2 * Chunk)]
    [InlineData(2 * Chunk + 1)]
    [InlineData(5 * Chunk - 1)]
    public async Task RoundTrip_AtEveryChunkBoundary_IsByteExact(int length)
    {
        var persistence = new InMemoryDataStreamPersistence();
        var payload = Bytes(length);

        var read = await StoreAndReadBackAsync(Store(persistence, Chunk), persistence, payload);

        read.ShouldBe(payload);
        persistence.Deleted.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(Chunk, 1)]
    [InlineData(Chunk + 1, 2)]
    [InlineData(3 * Chunk, 3)]
    public async Task Store_RecordsLengthAndChunkCount(int length, int expectedChunks)
    {
        var persistence = new InMemoryDataStreamPersistence();
        var stream = DataStream.FromBytes(Bytes(length));

        await Store(persistence, Chunk).StoreDataStreams([stream], false, CancellationToken.None);

        var header = persistence.Streams[stream.Id].Header;
        header.Length.ShouldBe(length);
        header.ChunkCount.ShouldBe(expectedChunks);
        persistence.Streams[stream.Id].Chunks.Count.ShouldBe(expectedChunks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTrip_BothDirections_RequestWriterAndResponseReceiver(bool useReceiver)
    {
        var persistence = new InMemoryDataStreamPersistence();
        var payload = Bytes(3 * Chunk + 5);

        (await StoreAndReadBackAsync(Store(persistence, Chunk), persistence, payload, useReceiver)).ShouldBe(payload);
    }

    [Fact]
    public async Task Store_ManyStreamsInOneMessage_AreAllStoredAndRehydratedByTheirOwnIds()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var store = Store(persistence, Chunk);
        var payloads = new[] { Bytes(40, 1), Bytes(0, 2), Bytes(16, 3), Bytes(100, 4) };
        var streams = payloads.Select(DataStream.FromBytes).ToList();

        var metadata = await store.StoreDataStreams(streams, false, CancellationToken.None);

        persistence.BatchesBegun.ShouldBe(1, "one transaction per message");

        var rehydrate = streams.Select(s => new CapturingRehydrate(s.Id)).Reverse().ToList();
        await store.RehydrateDataStreams(metadata, rehydrate.Cast<IRehydrateDataStream>().ToList(), CancellationToken.None);

        for (var i = 0; i < payloads.Length; i++)
            (await rehydrate.Single(r => r.Id == streams[i].Id).ReadAllAndDisposeAsync()).ShouldBe(payloads[i]);
    }

    [Fact]
    public async Task Store_NoStreams_TouchesNoStorage()
    {
        var persistence = new InMemoryDataStreamPersistence();

        await Store(persistence).StoreDataStreams([], false, CancellationToken.None);

        persistence.BatchesBegun.ShouldBe(0);
    }

    // Halibut asks to rehydrate every response too, and most carry no streams: that must not cost a database round trip.
    [Fact]
    public async Task Rehydrate_NoStreams_TouchesNoStorage()
    {
        var store = new DistributedDataStreamStore(new ThrowingPersistence(), Keys(), new DataStreamStorageOptions(16, TimeSpan.FromMinutes(1)));

        await store.RehydrateDataStreams([1], [], CancellationToken.None);
    }

    [Fact]
    public async Task SingleRead_DataIsDeletedWhenItsReaderIsDone_SoACollectedRequestCannotBeCollectedTwice()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var store = Store(persistence, Chunk);
        var stream = DataStream.FromBytes(Bytes(50));
        await store.StoreDataStreams([stream], false, CancellationToken.None);

        var first = new CapturingRehydrate(stream.Id);
        await store.RehydrateDataStreams([1], [first], CancellationToken.None);

        persistence.Streams.ShouldContainKey(stream.Id, "rehydrating alone must not consume the stream: Halibut reads it later, when it sends");

        await first.ReadAllAndDisposeAsync();

        persistence.Streams.ShouldNotContainKey(stream.Id);
        persistence.Deleted.ShouldBe([stream.Id]);
        await Should.ThrowAsync<DataStreamNotFoundException>(() => store.RehydrateDataStreams([1], [new CapturingRehydrate(stream.Id)], CancellationToken.None));
    }

    [Fact]
    public async Task Delete_HappensEvenWhenTheReaderStopsEarly()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var store = Store(persistence, Chunk);
        var stream = DataStream.FromBytes(Bytes(50));
        await store.StoreDataStreams([stream], false, CancellationToken.None);
        var rehydrate = new CapturingRehydrate(stream.Id);
        await store.RehydrateDataStreams([1], [rehydrate], CancellationToken.None);

        await using (var data = rehydrate.Supply())
            await data.Data.ReadAsync(new byte[3]);

        persistence.Streams.ShouldNotContainKey(stream.Id);
    }

    [Fact]
    public async Task Rehydrate_MissingStream_ThrowsNotFound_NamesIt_AndDeletesAndRehydratesNothing()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var store = Store(persistence, Chunk);
        var present = DataStream.FromBytes(Bytes(20));
        await store.StoreDataStreams([present], false, CancellationToken.None);
        var missingId = Guid.NewGuid();
        var good = new CapturingRehydrate(present.Id);
        var bad = new CapturingRehydrate(missingId);

        var ex = await Should.ThrowAsync<DataStreamNotFoundException>(() => store.RehydrateDataStreams([1], [good, bad], CancellationToken.None));

        ex.StreamId.ShouldBe(missingId);
        ex.ShouldBeAssignableTo<InvalidOperationException>("the same exception type Halibut's own sample stores throw");
        ex.Message.ShouldContain(missingId.ToString());
        good.Rehydrated.ShouldBeFalse();
        persistence.Streams.ShouldContainKey(present.Id);
    }

    [Fact]
    public async Task Store_WhenTheWriterThrowsMidStream_LeavesNoStreamBehind_NotEvenTheEarlierOnesOfTheMessage()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var good = DataStream.FromBytes(Bytes(40));
        var failing = new DataStream(100, async (s, ct) =>
        {
            await s.WriteAsync(Bytes(3 * Chunk + 3), ct);
            throw new IOException("writer failed halfway");
        });

        await Should.ThrowAsync<IOException>(() => Store(persistence, Chunk).StoreDataStreams([good, failing], false, CancellationToken.None));

        persistence.Streams.ShouldBeEmpty();
    }

    [Fact]
    public async Task Store_AlreadyCancelled_ThrowsOperationCanceled_AndStoresNothing()
    {
        var persistence = new InMemoryDataStreamPersistence();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => Store(persistence, Chunk).StoreDataStreams([DataStream.FromBytes(Bytes(40))], false, cts.Token));

        persistence.Streams.ShouldBeEmpty();
    }

    [Fact]
    public async Task Store_CancelledWhileTheWriterIsProducing_StopsAndStoresNothing()
    {
        var persistence = new InMemoryDataStreamPersistence();
        using var cts = new CancellationTokenSource();
        var slow = new DataStream(1000, async (s, ct) =>
        {
            await s.WriteAsync(Bytes(2 * Chunk), ct);
            cts.Cancel();
            await s.WriteAsync(Bytes(2 * Chunk), ct);
        });

        await Should.ThrowAsync<OperationCanceledException>(() => Store(persistence, Chunk).StoreDataStreams([slow], false, cts.Token));

        persistence.Streams.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rehydrate_Cancelled_Throws()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var store = Store(persistence, Chunk);
        var stream = DataStream.FromBytes(Bytes(20));
        await store.StoreDataStreams([stream], false, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => store.RehydrateDataStreams([1], [new CapturingRehydrate(stream.Id)], cts.Token));
    }

    [Fact]
    public async Task Read_Cancelled_Throws()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var store = Store(persistence, Chunk);
        var stream = DataStream.FromBytes(Bytes(50));
        await store.StoreDataStreams([stream], false, CancellationToken.None);
        var rehydrate = new CapturingRehydrate(stream.Id);
        await store.RehydrateDataStreams([1], [rehydrate], CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => rehydrate.ReadAllAndDisposeAsync(cts.Token));
    }

    [Fact]
    public async Task Store_SameIdTwice_ReplacesTheEarlierContent()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var store = Store(persistence, Chunk);
        var first = new DataStream(10, (s, ct) => s.WriteAsync(Bytes(40, 1), ct).AsTask()) { Id = Guid.NewGuid() };
        var second = new DataStream(10, (s, ct) => s.WriteAsync(Bytes(20, 2), ct).AsTask()) { Id = first.Id };

        await store.StoreDataStreams([first], false, CancellationToken.None);
        await store.StoreDataStreams([second], false, CancellationToken.None);

        var rehydrate = new CapturingRehydrate(first.Id);
        await store.RehydrateDataStreams([1], [rehydrate], CancellationToken.None);
        (await rehydrate.ReadAllAndDisposeAsync()).ShouldBe(Bytes(20, 2));
    }

    [Fact]
    public async Task AtRest_NoPlaintextInAnyChunk_NorTheKeyInTheClear()
    {
        const string marker = "SUPER-SECRET-VARIABLE-VALUE-0123456789";
        var persistence = new InMemoryDataStreamPersistence();
        var keys = Keys();
        var plaintext = Encoding.UTF8.GetBytes($"{{\"Password\":\"{marker}\"}}" + new string('x', 200));
        var stream = DataStream.FromBytes(plaintext);

        await Store(persistence, Chunk, keys).StoreDataStreams([stream], false, CancellationToken.None);

        var stored = persistence.Streams[stream.Id];
        var everything = stored.Chunks.Values.SelectMany(c => c).ToArray();
        IndexOf(everything, Encoding.UTF8.GetBytes(marker)).ShouldBe(-1, "ciphertext must not contain the plaintext");
        IndexOf(everything, Encoding.UTF8.GetBytes("Password")).ShouldBe(-1);
        stored.Header.ProtectedKey.ShouldStartWith(VariableEncryptionService.EncryptionPrefixV2, Case.Sensitive);
        keys.Unwrap(stored.Header.ProtectedKey, stream.Id).Length.ShouldBe(DataStreamCodec.KeyBytes);
    }

    [Fact]
    public async Task AtRest_EachStoreUsesAFreshKey_AndIdenticalContentSealsDifferently()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var store = Store(persistence, Chunk);
        var a = DataStream.FromBytes(Bytes(32));
        var b = DataStream.FromBytes(Bytes(32));

        await store.StoreDataStreams([a], false, CancellationToken.None);
        await store.StoreDataStreams([b], false, CancellationToken.None);

        persistence.Streams[a.Id].Header.ProtectedKey.ShouldNotBe(persistence.Streams[b.Id].Header.ProtectedKey);
        persistence.Streams[a.Id].Chunks[0].ShouldNotBe(persistence.Streams[b.Id].Chunks[0]);
    }

    [Fact]
    public async Task Store_WhenTheMasterKeyIsUnavailable_FailsClosed_StoringNothing()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var unavailable = new DataStreamKeyProtector(new Lazy<IVariableEncryptionService>(() => throw new InvalidOperationException("MasterKey is empty or missing")));

        await Should.ThrowAsync<InvalidOperationException>(() => Store(persistence, Chunk, unavailable).StoreDataStreams([DataStream.FromBytes(Bytes(20))], false, CancellationToken.None));

        persistence.BatchesBegun.ShouldBe(0);
        persistence.Streams.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rehydrate_UnderADifferentMasterKey_ThrowsUnreadable_NotGarbage()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var stream = DataStream.FromBytes(Bytes(40));
        await Store(persistence, Chunk, Keys(seed: 1)).StoreDataStreams([stream], false, CancellationToken.None);

        var other = Store(persistence, Chunk, Keys(seed: 90));

        var ex = await Should.ThrowAsync<DataStreamUnreadableException>(() => other.RehydrateDataStreams([1], [new CapturingRehydrate(stream.Id)], CancellationToken.None));
        ex.StreamId.ShouldBe(stream.Id);
    }

    [Fact]
    public async Task Rehydrate_ReadsBackWhatWasStoredWithAnotherChunkSize()
    {
        var persistence = new InMemoryDataStreamPersistence();
        var payload = Bytes(100);
        var stream = DataStream.FromBytes(payload);
        await Store(persistence, 7).StoreDataStreams([stream], false, CancellationToken.None);
        var rehydrate = new CapturingRehydrate(stream.Id);

        await Store(persistence, 64).RehydrateDataStreams([1], [rehydrate], CancellationToken.None);

        (await rehydrate.ReadAllAndDisposeAsync()).ShouldBe(payload, "a pod whose chunk size was configured differently must still read what another pod wrote");
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle);

    private sealed class ThrowingPersistence : IDataStreamPersistence
    {
        public Task<IDataStreamBatchWriter> BeginBatchAsync(CancellationToken ct) => throw new InvalidOperationException("storage touched");

        public Task<IReadOnlyList<StoredDataStreamHeader>> GetHeadersAsync(IReadOnlyCollection<Guid> streamIds, CancellationToken ct) => throw new InvalidOperationException("storage touched");

        public Task<byte[]> ReadChunkAsync(Guid streamId, int chunkIndex, CancellationToken ct) => throw new InvalidOperationException("storage touched");

        public Task DeleteAsync(Guid streamId, CancellationToken ct) => throw new InvalidOperationException("storage touched");

        public Task<int> DeleteOlderThanAsync(TimeSpan age, CancellationToken ct) => throw new InvalidOperationException("storage touched");
    }
}
