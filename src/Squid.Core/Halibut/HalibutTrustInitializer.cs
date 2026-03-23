using Halibut;
using Squid.Core.Services.Machines;

namespace Squid.Core.Halibut;

public class HalibutTrustInitializer : IStartable
{
    private readonly ILifetimeScope _scope;
    private HalibutRuntime _halibutRuntime;

    public HalibutTrustInitializer(ILifetimeScope scope)
    {
        _scope = scope;
    }

    public void Start()
    {
        if (!ResolveHalibutRuntime()) return;

        var machineDataProvider = _scope.Resolve<IMachineDataProvider>();
        var machines = machineDataProvider.GetTrustedPollingMachinesAsync().GetAwaiter().GetResult();

        foreach (var machine in machines)
        {
            _halibutRuntime.Trust(machine.Thumbprint);

            Log.Information("Trusted agent thumbprint for machine {MachineName} ({SubscriptionId})", machine.Name, machine.PollingSubscriptionId);
        }

        Log.Information("Halibut trust initialization complete, trusted {Count} agent(s)", machines.Count);
    }

    public void TrustThumbprint(string thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint)) return;
        if (!ResolveHalibutRuntime()) return;

        _halibutRuntime.Trust(thumbprint);

        Log.Information("Trusted new agent thumbprint {Thumbprint}", thumbprint);
    }

    public void RemoveTrust(string thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint)) return;
        if (!ResolveHalibutRuntime()) return;

        _halibutRuntime.RemoveTrust(thumbprint);

        Log.Information("Removed trust for agent thumbprint {Thumbprint}", thumbprint);
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
            Log.Warning(ex, "Halibut trust initialization skipped — HalibutRuntime not available");
            return false;
        }
    }
}
