namespace Squid.Message.Models.Deployments.LifeCycle;

public class LifecycleDetailDto
{
    public LifeCycleDto Lifecycle { get; set; }

    public List<LifecyclePhaseDto> Phases { get; set; }
}
