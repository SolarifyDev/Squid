using Halibut;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Halibut;

public class HalibutTrustInitializer : IStartable
{
    private readonly HalibutRuntime _halibutRuntime;
    private readonly IRepository _repository;

    public HalibutTrustInitializer(HalibutRuntime halibutRuntime, IRepository repository)
    {
        _halibutRuntime = halibutRuntime;
        _repository = repository;
    }

    public void Start()
    {
        var machines = _repository
            .QueryNoTracking<Machine>(m =>
                !string.IsNullOrEmpty(m.PollingSubscriptionId) &&
                !string.IsNullOrEmpty(m.Thumbprint) &&
                !m.IsDisabled)
            .ToList();

        foreach (var machine in machines)
        {
            _halibutRuntime.Trust(machine.Thumbprint);

            Log.Information("Trusted agent thumbprint for machine {MachineName} ({SubscriptionId})",
                machine.Name, machine.PollingSubscriptionId);
        }

        Log.Information("Halibut trust initialization complete, trusted {Count} agent(s)", machines.Count);
    }
}
