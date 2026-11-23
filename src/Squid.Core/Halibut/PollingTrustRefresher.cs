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
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _refreshGate = new();
    private readonly object _unauthorizedGate = new();
    private Timer _timer;
    private bool _running;
    private bool _pending;
    private bool _hasUnauthorizedRefresh;
    private long _lastUnauthorizedRefresh;
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
        if (!TryClaimUnauthorizedRefresh()) return;

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

    private bool TryClaimUnauthorizedRefresh()
    {
        var now = _time.GetTimestamp();

        lock (_unauthorizedGate)
        {
            if (_hasUnauthorizedRefresh && _time.GetElapsedTime(_lastUnauthorizedRefresh, now) < _settings.UnauthorizedRefreshMinInterval) return false;

            _hasUnauthorizedRefresh = true;
            _lastUnauthorizedRefresh = now;

            return true;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _stopping.Cancel();
        _timer?.Dispose();
        _stopping.Dispose();
    }
}
