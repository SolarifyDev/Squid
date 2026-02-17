using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments;

public interface IActionHandlerRegistry : IScopedDependency
{
    IActionHandler Resolve(DeploymentActionDto action);
}

public class ActionHandlerRegistry : IActionHandlerRegistry
{
    private readonly IEnumerable<IActionHandler> _handlers;

    public ActionHandlerRegistry(IEnumerable<IActionHandler> handlers)
    {
        _handlers = handlers;
    }

    public IActionHandler Resolve(DeploymentActionDto action)
    {
        return _handlers.FirstOrDefault(h => h.CanHandle(action));
    }
}
