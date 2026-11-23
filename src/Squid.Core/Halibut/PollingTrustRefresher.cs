using Squid.Core.Settings.Halibut;

namespace Squid.Core.Halibut;

public interface IPollingTrustRefresher
{
    /// <summary>
    /// Called when a client whose certificate is not in this server's trust list connects. It may be an agent that was registered through
    /// another replica, so a (rate-limited, asynchronous) refresh from the database is requested. Never blocks and never throws; the
    /// connection itself is still rejected, and the agent's next reconnect finds the refreshed list.
    /// </summary>
    void OnUnauthorizedClient(string thumbprint);
}

/// <summary>
/// Keeps this server's trust list converged with the database when other replicas change it: refreshes on a timer
/// (<see cref="TrustSettings.RefreshInterval"/>) and on demand when an unknown client connects. Every refresh goes through
/// <see cref="IPollingTrustDistributor.ReconfigureAsync"/>, which serialises read-and-apply, so a slower older refresh can never overwrite a
/// newer list; a refresh that throws leaves the previous list untouched.
/// </summary>
public sealed class PollingTrustRefresher : IPollingTrustRefresher, IStartable, IDisposable
{
    private readonly IPollingTrustDistributor _distributor;
    private readonly TrustSettings _settings;
    private readonly TimeProvider _time;
    private const int MaxTrackedClients = 1024;

    private readonly CancellationTokenSource _stopping = new();
    private readonly object _refreshGate = new();
    private readonly object _unauthorizedGate = new();
    private Timer _timer;
    private bool _running;
    private bool _pending;
    private bool _hasUnauthorizedRefresh;
    private long _lastUnauthorizedRefresh;
    private readonly Dictionary<string, ClientBackoff> _clientBackoff = new();
    private int _disposed;

    public PollingTrustRefresher(IPollingTrustDistributor distributor, HalibutSetting setting = null) : this(distributor, setting?.Trust, TimeProvider.System)
    {
    }

    internal PollingTrustRefresher(IPollingTrustDistributor distributor, TrustSettings settings, TimeProvider time)
    {
        _distributor = distributor;
        _settings = settings ?? new TrustSettings();
        _time = time;
    }

    public void Start()
    {
        var interval = _settings.RefreshInterval;

        _timer = new Timer(_ => _ = RefreshAsync(), null, interval, interval);
    }

    public void OnUnauthorizedClient(string thumbprint)
    {
        if (!TryClaimUnauthorizedRefresh(thumbprint)) return;

        Log.Debug("Unknown Halibut client {Thumbprint} connected, refreshing the polling trust list from the database", thumbprint);

        _ = Task.Run(RefreshAsync);
    }

    /// <summary>
    /// Runs a refresh; a request that arrives while one is in flight is coalesced into exactly one follow-up run, because the in-flight read
    /// may have started before the change that prompted the request.
    /// </summary>
    internal async Task RefreshAsync()
    {
        lock (_refreshGate)
        {
            if (_running)
            {
                _pending = true;
                return;
            }

            _running = true;
        }

        while (true)
        {
            await RefreshOnceSafelyAsync().ConfigureAwait(false);

            lock (_refreshGate)
            {
                if (!_pending || _stopping.IsCancellationRequested)
                {
                    _running = false;
                    return;
                }

                _pending = false;
            }
        }
    }

    private async Task RefreshOnceSafelyAsync()
    {
        try
        {
            await _distributor.ReconfigureAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Halibut trust refresh failed, keeping the previous trust list; the next refresh retries");
        }
    }

    // Two gates: a global minimum gap (a burst of unknown clients costs one read) and a per-thumbprint backoff. A client that stays
    // unknown after a refresh (a deleted machine whose agent keeps polling) would otherwise trigger a database read every global gap forever;
    // its gap doubles on each repeat up to the periodic refresh interval, which already re-reads the list at that rate anyway.
    private bool TryClaimUnauthorizedRefresh(string thumbprint)
    {
        var now = _time.GetTimestamp();
        var key = thumbprint ?? string.Empty;

        lock (_unauthorizedGate)
        {
            if (_hasUnauthorizedRefresh && _time.GetElapsedTime(_lastUnauthorizedRefresh, now) < _settings.UnauthorizedRefreshMinInterval) return false;
            if (_clientBackoff.TryGetValue(key, out var previous) && _time.GetElapsedTime(previous.At, now) < previous.Gap) return false;

            _clientBackoff[key] = NextBackoff(previous, now);
            PruneClientBackoff(now);

            _hasUnauthorizedRefresh = true;
            _lastUnauthorizedRefresh = now;

            return true;
        }
    }

    private ClientBackoff NextBackoff(ClientBackoff previous, long now)
    {
        var min = _settings.UnauthorizedRefreshMinInterval;
        var cap = Max(_settings.RefreshInterval, min);

        if (previous == null || _time.GetElapsedTime(previous.At, now) > cap * 2) return new ClientBackoff(now, min);

        var doubled = previous.Gap * 2;

        return new ClientBackoff(now, doubled < cap ? doubled : cap);
    }

    private void PruneClientBackoff(long now)
    {
        if (_clientBackoff.Count <= MaxTrackedClients) return;

        var stale = _clientBackoff.Where(kv => _time.GetElapsedTime(kv.Value.At, now) > _settings.RefreshInterval * 2).Select(kv => kv.Key).ToList();

        foreach (var key in stale) _clientBackoff.Remove(key);

        if (_clientBackoff.Count > MaxTrackedClients) _clientBackoff.Clear();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private sealed record ClientBackoff(long At, TimeSpan Gap);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _stopping.Cancel();
        _timer?.Dispose();
        _stopping.Dispose();
    }
}
