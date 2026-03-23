using Halibut;
using Squid.Core.Services.Machines;

namespace Squid.Core.Halibut;

public interface IPollingTrustDistributor
{
    void Reconfigure();

    void ReconfigureIfMissing(string thumbprint);
}

public class PollingTrustDistributor : IPollingTrustDistributor, IStartable
{
    private readonly ILifetimeScope _scope;
    private HalibutRuntime _halibutRuntime;

    public PollingTrustDistributor(ILifetimeScope scope)
    {
        _scope = scope;
    }

    public void Start()
    {
        Reconfigure();
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
