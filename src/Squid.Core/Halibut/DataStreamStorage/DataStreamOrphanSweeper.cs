using Squid.Core.Settings.Halibut;

namespace Squid.Core.Halibut.DataStreamStorage;

public interface IDataStreamOrphanSweeper : IScopedDependency
{
    /// <summary>Removes the stored streams nobody collected within the TTL and returns how many.</summary>
    Task<int> SweepAsync(CancellationToken ct = default);
}

/// <summary>
/// Halibut gives no signal when a queued request is cancelled, times out or is never collected, so its stored streams would stay for ever. Age decides, by the
/// database's own clock (pods' clocks may differ): a stream older than the TTL, which is longer than any request waits for pickup, belongs to no live request.
/// </summary>
public sealed class DataStreamOrphanSweeper : IDataStreamOrphanSweeper
{
    private readonly IDataStreamPersistence _persistence;
    private readonly DataStreamStorageOptions _options;

    public DataStreamOrphanSweeper(IDataStreamPersistence persistence, HalibutSetting setting) : this(persistence, DataStreamStorageOptions.From(setting))
    {
    }

    internal DataStreamOrphanSweeper(IDataStreamPersistence persistence, DataStreamStorageOptions options)
    {
        _persistence = persistence;
        _options = options;
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var removed = await _persistence.DeleteOlderThanAsync(_options.OrphanTtl, ct).ConfigureAwait(false);

        if (removed > 0) Log.Information("Removed {Removed} orphaned Halibut DataStreams older than {OrphanTtl}", removed, _options.OrphanTtl);

        return removed;
    }
}
