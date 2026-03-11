using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution;

public interface IActionHandlerRegistry : IScopedDependency
{
    IActionHandler Resolve(DeploymentActionDto action);

    ExecutionScope ResolveScope(DeploymentActionDto action);
}

public class ActionHandlerRegistry : IActionHandlerRegistry
{
    private readonly IReadOnlyDictionary<DeploymentActionType, IActionHandler> _handlers;

    public ActionHandlerRegistry(IEnumerable<IActionHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _handlers = handlers.ToDictionary(h => h.ActionType);
    }

    public IActionHandler Resolve(DeploymentActionDto action)
    {
        if (action == null || !DeploymentActionTypeParser.TryParse(action.ActionType, out var actionType))
            return null;

        if (!_handlers.TryGetValue(actionType, out var handler))
            return null;

        return handler.CanHandle(action) ? handler : null;
    }

    public ExecutionScope ResolveScope(DeploymentActionDto action)
    {
        var handler = Resolve(action);

        return handler?.ExecutionScope ?? ExecutionScope.TargetLevel;
    }
}
