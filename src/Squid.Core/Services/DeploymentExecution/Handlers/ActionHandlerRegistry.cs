using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Handlers;

public interface IActionHandlerRegistry : IScopedDependency
{
    IActionHandler Resolve(DeploymentActionDto action);

    ExecutionScope ResolveScope(DeploymentActionDto action);
}

public class ActionHandlerRegistry : IActionHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IActionHandler> _handlers;

    public ActionHandlerRegistry(IEnumerable<IActionHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _handlers = handlers.ToDictionary(h => h.ActionType, StringComparer.OrdinalIgnoreCase);
    }

    public IActionHandler Resolve(DeploymentActionDto action)
    {
        if (string.IsNullOrEmpty(action?.ActionType)) return null;

        if (!_handlers.TryGetValue(action.ActionType, out var handler)) return null;

        return handler.CanHandle(action) ? handler : null;
    }

    public ExecutionScope ResolveScope(DeploymentActionDto action)
    {
        var handler = Resolve(action);

        return handler?.ExecutionScope ?? ExecutionScope.TargetLevel;
    }
}
