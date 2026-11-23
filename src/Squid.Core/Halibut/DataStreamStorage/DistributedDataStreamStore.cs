using Halibut;
using Halibut.Queue.QueuedDataStreams;
using Halibut.Queue.Redis.MessageStorage;
using Squid.Core.Halibut.DataStreamStorage.Exceptions;
using Squid.Core.Settings.Halibut;

namespace Squid.Core.Halibut.DataStreamStorage;

/// <summary>
/// Moves the data streams of a queued Halibut request between the pod that took the request and the pod that holds the agent's polling connection,
/// through the shared database. Halibut calls <see cref="StoreDataStreams"/> once per message before queueing it and <see cref="RehydrateDataStreams"/> once on
/// the pod that collects it; it never says when a message is cancelled, times out or is never collected, so a stored stream is deleted when its reader is
/// done and the orphan sweep removes the rest. The whole message is stored in one transaction (none of it is visible until all of it is stored, and a failure
/// leaves nothing), sealed chunk by chunk with a fresh data key that only the master key can open, so no secret is ever at rest in the clear.
/// </summary>
public sealed class DistributedDataStreamStore : IStoreDataStreamsForDistributedQueues, ISingletonDependency
{
    /// <summary>What Halibut keeps in the queued message instead of the data: only a format marker, since the streams are found by their ids.</summary>
    internal static readonly byte[] Metadata = [1];

    private readonly IDataStreamPersistence _persistence;
    private readonly IDataStreamKeyProtector _keys;
    private readonly DataStreamStorageOptions _options;

    public DistributedDataStreamStore(IDataStreamPersistence persistence, IDataStreamKeyProtector keys, HalibutSetting setting) : this(persistence, keys, DataStreamStorageOptions.From(setting))
    {
    }

    internal DistributedDataStreamStore(IDataStreamPersistence persistence, IDataStreamKeyProtector keys, DataStreamStorageOptions options)
    {
        _persistence = persistence;
        _keys = keys;
        _options = options;
    }

    public async Task<byte[]> StoreDataStreams(IReadOnlyList<DataStream> dataStreams, bool useReceiver, CancellationToken cancellationToken)
    {
        if (dataStreams.Count == 0) return Metadata;

        var key = _keys.NewKey();
        var protectedKey = _keys.Wrap(key);

        await using var batch = await _persistence.BeginBatchAsync(cancellationToken).ConfigureAwait(false);

        foreach (var dataStream in dataStreams)
            await StoreOneAsync(batch, dataStream, new StoreContext(key, protectedKey, useReceiver), cancellationToken).ConfigureAwait(false);

        await batch.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Metadata;
    }

    private async Task StoreOneAsync(IDataStreamBatchWriter batch, DataStream dataStream, StoreContext context, CancellationToken ct)
    {
        await batch.BeginStreamAsync(dataStream.Id, context.ProtectedKey, ct).ConfigureAwait(false);

        await using var writer = new DataStreamChunkWriter(batch, dataStream.Id, context.Key, _options.ChunkBytes);

        await CopyIntoAsync(dataStream, writer, context.UseReceiver, ct).ConfigureAwait(false);
        await writer.CompleteAsync(ct).ConfigureAwait(false);
    }

    // A request's streams are produced by their writer; a response's arrive through their receiver.
    private static Task CopyIntoAsync(DataStream dataStream, Stream destination, bool useReceiver, CancellationToken ct)
        => useReceiver ? dataStream.Receiver().ReadAsync((source, token) => source.CopyToAsync(destination, token), ct) : dataStream.WriteData(destination, ct);

    public async Task RehydrateDataStreams(byte[] dataStreamMetadata, List<IRehydrateDataStream> dataStreams, CancellationToken cancellationToken)
    {
        if (dataStreams.Count == 0) return;

        var ids = dataStreams.Select(s => s.Id).Distinct().ToList();
        var headers = await LoadHeadersAsync(ids, cancellationToken).ConfigureAwait(false);
        var keys = UnwrapKeys(headers.Values);

        foreach (var dataStream in dataStreams)
            Rehydrate(dataStream, headers[dataStream.Id], keys[headers[dataStream.Id].ProtectedKey]);
    }

    private async Task<Dictionary<Guid, StoredDataStreamHeader>> LoadHeadersAsync(List<Guid> ids, CancellationToken ct)
    {
        var headers = (await _persistence.GetHeadersAsync(ids, ct).ConfigureAwait(false)).ToDictionary(h => h.StreamId);
        var missing = ids.FirstOrDefault(id => !headers.ContainsKey(id), Guid.Empty);

        if (missing != Guid.Empty)
            throw new DataStreamNotFoundException(missing);

        return headers;
    }

    // A message's streams share one wrapped key, so it is unwrapped (PBKDF2) once, not once per stream.
    private Dictionary<string, byte[]> UnwrapKeys(IEnumerable<StoredDataStreamHeader> headers)
        => headers.GroupBy(h => h.ProtectedKey).ToDictionary(g => g.Key, g => _keys.Unwrap(g.Key, g.First().StreamId));

    private void Rehydrate(IRehydrateDataStream dataStream, StoredDataStreamHeader header, byte[] key)
        => dataStream.Rehydrate(() => new DataStreamRehydrationData(new DataStreamReadStream(_persistence, header, key), new DeleteWhenDisposed(_persistence, header.StreamId)));

    private sealed record StoreContext(byte[] Key, string ProtectedKey, bool UseReceiver);

    /// <summary>Deletes the stream once its reader is finished with it, whether it read to the end or not: a collected request is not collected twice. A failed delete is left to the orphan sweep.</summary>
    private sealed class DeleteWhenDisposed(IDataStreamPersistence persistence, Guid streamId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await persistence.DeleteAsync(streamId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not delete the stored Halibut DataStream {StreamId}; the orphan sweep will remove it", streamId);
            }
        }
    }
}
