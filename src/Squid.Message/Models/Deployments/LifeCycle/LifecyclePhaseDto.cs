namespace Squid.Message.Models.Deployments.LifeCycle;

public class LifecyclePhaseDto
{
    public LifeCycleDto Lifecycle { get; set; }

    public List<PhaseDto> Phases { get; set; }
}