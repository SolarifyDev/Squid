using Squid.Message.Enums.Deployments;

namespace Squid.Core.Persistence.Entities.Deployments;

public class LifecyclePhaseEnvironment : IEntity
{
    public int PhaseId { get; set; }

    public int EnvironmentId { get; set; }

    public LifecyclePhaseEnvironmentTargetType TargetType { get; set; }
}
