using Halibut;
using Squid.Core.Services.Machines;

namespace Squid.Core.Halibut;

public class HalibutTrustInitializer : IStartable
{
    private readonly ILifetimeScope _scope;

    public HalibutTrustInitializer(ILifetimeScope scope)
    {
        _scope = scope;
    }

    public void Start()
    {
        HalibutRuntime halibutRuntime;

        try
        {
            halibutRuntime = _scope.Resolve<HalibutRuntime>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Halibut trust initialization skipped");
            return;
        }

        var machineDataProvider = _scope.Resolve<IMachineDataProvider>();
        var machines = machineDataProvider.GetTrustedPollingMachinesAsync().GetAwaiter().GetResult();

        foreach (var machine in machines)
        {
            halibutRuntime.Trust(machine.Thumbprint);

            Log.Information("Trusted agent thumbprint for machine {MachineName} ({SubscriptionId})", machine.Name, machine.PollingSubscriptionId);
        }

        Log.Information("Halibut trust initialization complete, trusted {Count} agent(s)", machines.Count);
    }
}
