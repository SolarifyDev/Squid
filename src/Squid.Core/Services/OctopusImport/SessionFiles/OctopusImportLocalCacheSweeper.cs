namespace Squid.Core.Services.OctopusImport.SessionFiles;

/// <summary>
/// Removes this server's cached copies of import session files once the session was cleaned up. The cleanup job runs on one server
/// and deletes the stored files and that server's copy; every other server that cached the export upload or the bundle (which may hold
/// secrets) removes its own copy here. Only directories of sessions this database knows as cleaned are touched: a directory of an
/// unknown session may belong to another Squid sharing the same temporary root, and is left alone.
/// </summary>
public interface IOctopusImportLocalCacheSweep : IScopedDependency
{
    /// <returns>How many session directories were removed.</returns>
    Task<int> SweepAsync(CancellationToken ct = default);
}

public sealed class OctopusImportLocalCacheSweep(IOctopusImportTemporaryUploadSettings settings, IOctopusImportTemporaryUploadStore uploadStore, IOctopusImportSessionFileStore files) : IOctopusImportLocalCacheSweep
{
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var cached = CachedSessionIds();

        if (cached.Count == 0) return 0;

        var purged = await files.PurgedAmongAsync(cached, ct).ConfigureAwait(false);

        foreach (var sessionId in purged)
            await uploadStore.SecureDeleteSessionDirectoryAsync(sessionId, ct).ConfigureAwait(false);

        return purged.Count;
    }

    // Session directories are named by the session id ("N" format); anything else under the root is not this store's.
    private List<Guid> CachedSessionIds()
    {
        var root = Path.GetFullPath(settings.RootPath);

        if (!Directory.Exists(root)) return [];

        return Directory.EnumerateDirectories(root).Select(Path.GetFileName).Select(name => Guid.TryParseExact(name, "N", out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
    }
}

/// <summary>Runs <see cref="IOctopusImportLocalCacheSweep"/> on every server, every <see cref="Interval"/>; a failed sweep is logged and retried next time.</summary>
public sealed class OctopusImportLocalCacheSweeper(ILifetimeScope container) : IStartable, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private Timer _timer;

    public void Start() => _timer = new Timer(_ => _ = SweepSafelyAsync(), null, Interval, Interval);

    private async Task SweepSafelyAsync()
    {
        try
        {
            await using var scope = container.BeginLifetimeScope();

            var removed = await scope.Resolve<IOctopusImportLocalCacheSweep>().SweepAsync().ConfigureAwait(false);

            if (removed > 0) Log.Information("[OctopusImport] Removed this server's cached files of {Count} cleaned-up import session(s)", removed);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OctopusImport] Could not remove this server's cached import session files; the next sweep retries");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
