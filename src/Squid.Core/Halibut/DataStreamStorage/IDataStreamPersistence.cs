namespace Squid.Core.Halibut.DataStreamStorage;

/// <summary>What is known about a stored stream without reading its chunks. The key is wrapped; only the key protector can open it.</summary>
public sealed record StoredDataStreamHeader(Guid StreamId, long Length, int ChunkCount, string ProtectedKey);

/// <summary>Where sealed chunks of data streams live. Knows nothing of keys, sealing or Halibut: it stores and returns bytes.</summary>
public interface IDataStreamPersistence
{
    /// <summary>Starts an all-or-nothing write: nothing in it is visible to readers until it is committed, and a batch disposed uncommitted leaves nothing behind.</summary>
    Task<IDataStreamBatchWriter> BeginBatchAsync(CancellationToken ct);

    /// <summary>The headers of those of <paramref name="streamIds"/> that are stored; absent ids are simply missing from the result.</summary>
    Task<IReadOnlyList<StoredDataStreamHeader>> GetHeadersAsync(IReadOnlyCollection<Guid> streamIds, CancellationToken ct);

    /// <summary>The sealed chunk, or null when it is not stored.</summary>
    Task<byte[]> ReadChunkAsync(Guid streamId, int chunkIndex, CancellationToken ct);

    Task DeleteAsync(Guid streamId, CancellationToken ct);

    /// <summary>Deletes every stream stored longer ago than <paramref name="age"/> by the database's clock, and returns how many.</summary>
    Task<int> DeleteOlderThanAsync(TimeSpan age, CancellationToken ct);
}

/// <summary>One all-or-nothing write of several streams. Disposing without <see cref="CommitAsync"/> discards everything written.</summary>
public interface IDataStreamBatchWriter : IAsyncDisposable
{
    /// <summary>Begins <paramref name="streamId"/>, replacing a stream already stored under that id.</summary>
    Task BeginStreamAsync(Guid streamId, string protectedKey, CancellationToken ct);

    Task AddChunkAsync(Guid streamId, int chunkIndex, ReadOnlyMemory<byte> sealedChunk, CancellationToken ct);

    Task CompleteStreamAsync(Guid streamId, long length, int chunkCount, CancellationToken ct);

    Task CommitAsync(CancellationToken ct);
}
