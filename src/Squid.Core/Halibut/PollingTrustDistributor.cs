using Halibut;
using Squid.Core.Services.Machines;

namespace Squid.Core.Halibut;

public interface IPollingTrustDistributor
{
    bool InitialLoadCompleted { get; }

    void Reconfigure();

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
                Reconfigure();
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

    public void Reconfigure()
    {
        if (!ResolveHalibutRuntime()) return;

        var dataProvider = _scope.Resolve<IMachineDataProvider>();
        var thumbprints = dataProvider.GetPollingThumbprintsAsync().GetAwaiter().GetResult();

        _halibutRuntime.TrustOnly(thumbprints);

        Log.Information("Halibut trust reconfigured, {Count} polling agent(s) trusted", thumbprints.Count);
    }

    public void ReconfigureIfMissing(string thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint)) return;
        if (!ResolveHalibutRuntime()) return;
        if (_halibutRuntime.IsTrusted(thumbprint)) return;

        Reconfigure();
    }

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
