namespace Squid.Message.Models.Deployments.LifeCycle;

public class CreateOrUpdateLifeCycleModel
{
    public LifeCycleModel Lifecycle { get; set; }
    public List<LifecyclePhaseModel> Phases { get; set; } = new();
}
