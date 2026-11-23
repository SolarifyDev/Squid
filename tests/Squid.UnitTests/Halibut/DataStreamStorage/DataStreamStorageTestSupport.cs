using System.IO;
using System.Linq;
using Halibut;
using Halibut.Queue.Redis.MessageStorage;
using Microsoft.Extensions.Configuration;
using Squid.Core.Halibut.DataStreamStorage;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;

namespace Squid.UnitTests.Halibut.DataStreamStorage;

/// <summary>
/// Models what <see cref="IDataStreamPersistence"/> promises, no more: a batch's streams become visible all at once on commit and a batch disposed
/// uncommitted leaves nothing. Every call honours its cancellation token like a database call does.
/// </summary>
internal sealed class InMemoryDataStreamPersistence : IDataStreamPersistence
{
    public Dictionary<Guid, StoredStream> Streams { get; } = new();

    public List<Guid> Deleted { get; } = new();

    public int BatchesBegun { get; private set; }

    public Task<IDataStreamBatchWriter> BeginBatchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        BatchesBegun++;

        return Task.FromResult<IDataStreamBatchWriter>(new Batch(this));
    }

    public Task<IReadOnlyList<StoredDataStreamHeader>> GetHeadersAsync(IReadOnlyCollection<Guid> streamIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<StoredDataStreamHeader> headers = streamIds.Where(Streams.ContainsKey).Select(id => Streams[id].Header).ToList();

        return Task.FromResult(headers);
    }

    public Task<byte[]> ReadChunkAsync(Guid streamId, int chunkIndex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Streams.TryGetValue(streamId, out var stream) && stream.Chunks.TryGetValue(chunkIndex, out var chunk) ? chunk : null);
    }

    public Task DeleteAsync(Guid streamId, CancellationToken ct)
    {
        Streams.Remove(streamId);
        Deleted.Add(streamId);

        return Task.CompletedTask;
    }

    public TimeSpan? DeletedOlderThan { get; private set; }

    public int OlderThanResult { get; set; }

    public Task<int> DeleteOlderThanAsync(TimeSpan age, CancellationToken ct)
    {
        DeletedOlderThan = age;

        return Task.FromResult(OlderThanResult);
    }

    public sealed class StoredStream
    {
        public StoredDataStreamHeader Header { get; set; }
        public Dictionary<int, byte[]> Chunks { get; } = new();
    }

    private sealed class Batch(InMemoryDataStreamPersistence owner) : IDataStreamBatchWriter
    {
        private readonly Dictionary<Guid, StoredStream> _staged = new();

        public Task BeginStreamAsync(Guid streamId, string protectedKey, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            _staged[streamId] = new StoredStream { Header = new StoredDataStreamHeader(streamId, 0, 0, protectedKey) };

            return Task.CompletedTask;
        }

        public Task AddChunkAsync(Guid streamId, int chunkIndex, ReadOnlyMemory<byte> sealedChunk, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            _staged[streamId].Chunks[chunkIndex] = sealedChunk.ToArray();

            return Task.CompletedTask;
        }

        public Task CompleteStreamAsync(Guid streamId, long length, int chunkCount, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            _staged[streamId].Header = _staged[streamId].Header with { Length = length, ChunkCount = chunkCount };

            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var (id, stream) in _staged) owner.Streams[id] = stream;

            _staged.Clear();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class CapturingRehydrate(Guid id, long length = 0) : IRehydrateDataStream
{
    private Func<DataStreamRehydrationData> _supplier;

    public Guid Id { get; } = id;

    public long Length { get; } = length;

    public bool Rehydrated => _supplier != null;

    public void Rehydrate(Func<DataStreamRehydrationData> dataStreamRehydratorFactory) => _supplier = dataStreamRehydratorFactory;

    public DataStreamRehydrationData Supply() => _supplier();

    /// <summary>What Halibut's copier does with it: reads every byte, then disposes the data.</summary>
    public async Task<byte[]> ReadAllAndDisposeAsync(CancellationToken ct = default)
    {
        await using var data = Supply();
        using var copy = new MemoryStream();

        await data.Data.CopyToAsync(copy, ct);

        return copy.ToArray();
    }
}

internal static class DataStreamStorageTestSupport
{
    public static VariableEncryptionService Encryption(byte seed = 1)
    {
        var masterKey = Enumerable.Range(0, 32).Select(i => (byte)(i + seed)).ToArray();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Security:VariableEncryption:MasterKey"] = Convert.ToBase64String(masterKey) }).Build();

        return new VariableEncryptionService(new SecuritySetting(config));
    }

    public static DataStreamKeyProtector Keys(byte seed = 1) => new(new Lazy<IVariableEncryptionService>(() => Encryption(seed)));

    public static DistributedDataStreamStore Store(InMemoryDataStreamPersistence persistence, int chunkBytes = 16, IDataStreamKeyProtector keys = null)
        => new(persistence, keys ?? Keys(), new DataStreamStorageOptions(chunkBytes, TimeSpan.FromMinutes(60)));

    public static byte[] Bytes(int length, int seed = 7)
    {
        var bytes = new byte[length];

        new Random(seed).NextBytes(bytes);

        return bytes;
    }

    public static async Task<byte[]> StoreAndReadBackAsync(DistributedDataStreamStore store, InMemoryDataStreamPersistence persistence, byte[] payload, bool useReceiver = false)
    {
        var stream = DataStream.FromBytes(payload);

        await store.StoreDataStreams([stream], useReceiver, CancellationToken.None);

        var rehydrate = new CapturingRehydrate(stream.Id, payload.Length);

        await store.RehydrateDataStreams(DistributedDataStreamStore_Metadata, [rehydrate], CancellationToken.None);

        return await rehydrate.ReadAllAndDisposeAsync();
    }

    public static readonly byte[] DistributedDataStreamStore_Metadata = [1];
}
