using Halibut;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

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
            Log.Warning("Halibut trust initialization skipped: {Message}", ex.Message);
            return;
        }

        var repository = _scope.Resolve<IRepository>();

        var machines = repository
            .QueryNoTracking<Machine>(m =>
                !string.IsNullOrEmpty(m.PollingSubscriptionId) &&
                !string.IsNullOrEmpty(m.Thumbprint) &&
                !m.IsDisabled)
            .ToList();

        foreach (var machine in machines)
        {
            halibutRuntime.Trust(machine.Thumbprint);

            Log.Information("Trusted agent thumbprint for machine {MachineName} ({SubscriptionId})",
                machine.Name, machine.PollingSubscriptionId);
        }

        Log.Information("Halibut trust initialization complete, trusted {Count} agent(s)", machines.Count);
    }
}
