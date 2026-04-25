using Halibut;
using Squid.Core.Services.Machines;

namespace Squid.Core.Halibut;

public interface IPollingTrustDistributor
{
    bool InitialLoadCompleted { get; }

    /// <summary>
    /// Async reload of the polling-trust list — preferred entry point for any
    /// caller already inside an async method (controllers, mediator handlers).
    /// </summary>
    Task ReconfigureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Async fast-path for the post-registration trust check. Short-circuits
    /// when the thumbprint is already trusted; otherwise awaits a full
    /// <see cref="ReconfigureAsync"/>.
    /// </summary>
    Task ReconfigureIfMissingAsync(string thumbprint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sync wrapper kept for back-compat with the <see cref="IStartable"/>
    /// startup hook. New call sites should use <see cref="ReconfigureAsync"/>.
    /// </summary>
    void Reconfigure();

    /// <summary>
    /// Sync wrapper kept for back-compat. New call sites should use
    /// <see cref="ReconfigureIfMissingAsync"/> — the sync overload here will
    /// block the calling thread on the underlying DB query.
    /// </summary>
    void ReconfigureIfMissing(string thumbprint);
}

public class PollingTrustDistributor : IPollingTrustDistributor, IStartable
{
    private readonly ILifetimeScope _scope;
    private HalibutRuntime _halibutRuntime;
    private volatile bool _initialLoadCompleted;

    public PollingTrustDistributor(ILifetimeScope scope)
    {
        _scope = scope;
    }

    /// <summary>
    /// True once the initial DB-backed trust load has completed. Consumers that care
    /// about readiness (e.g. <c>/readyz</c>) should wait for this to flip before
    /// reporting healthy — until then, unknown polling agents will be rejected.
    /// </summary>
    public bool InitialLoadCompleted => _initialLoadCompleted;

    /// <summary>
    /// Schedules the first trust load on a background task so server startup is not
    /// blocked by a slow or unavailable database. During the warmup window, polling
    /// agents whose thumbprints are not yet loaded will be rejected at the TLS layer;
    /// <see cref="ReconfigureIfMissing"/> still works synchronously for new agent
    /// registrations that happen after startup.
    /// </summary>
    public void Start()
    {
        Task.Run(async () =>
        {
            try
            {
                await Task.Yield();
                await ReconfigureAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Halibut initial trust load failed — polling connections will be rejected until a successful Reconfigure");
            }
            finally
            {
                _initialLoadCompleted = true;
            }
        });
    }

    public async Task ReconfigureAsync(CancellationToken cancellationToken = default)
    {
        if (!ResolveHalibutRuntime()) return;

        var dataProvider = _scope.Resolve<IMachineDataProvider>();
        var thumbprints = await dataProvider.GetPollingThumbprintsAsync(cancellationToken).ConfigureAwait(false);

        _halibutRuntime.TrustOnly(thumbprints);

        Log.Information("Halibut trust reconfigured, {Count} polling agent(s) trusted", thumbprints.Count);
    }

    public async Task ReconfigureIfMissingAsync(string thumbprint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(thumbprint)) return;
        if (!ResolveHalibutRuntime()) return;
        if (_halibutRuntime.IsTrusted(thumbprint)) return;

        await ReconfigureAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// P1 (Phase-6, post-Phase-5 deep audit): the original sync entry point
    /// blocked Kestrel request threads via <c>.GetAwaiter().GetResult()</c>
    /// on the DB query when invoked from registration controllers. Kept for
    /// back-compat with non-async callers; new code paths must call
    /// <see cref="ReconfigureAsync"/> directly.
    /// </summary>
    public void Reconfigure() => ReconfigureAsync().GetAwaiter().GetResult();

    /// <summary>Same back-compat shim for <see cref="ReconfigureIfMissingAsync"/>.</summary>
    public void ReconfigureIfMissing(string thumbprint) => ReconfigureIfMissingAsync(thumbprint).GetAwaiter().GetResult();

    private bool ResolveHalibutRuntime()
    {
        if (_halibutRuntime != null) return true;

        try
        {
            _halibutRuntime = _scope.Resolve<HalibutRuntime>();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Halibut trust reconfiguration skipped — HalibutRuntime not available");
            return false;
        }
    }
}
