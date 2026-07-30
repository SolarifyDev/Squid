using Squid.Message.Constants;
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

        if (handler != null)
            return handler.ExecutionScope;

        // Package acquisition is an internally injected synthetic step. It has no
        // IActionHandler because ExecuteStepsPhase handles it directly.
        if (string.Equals(action?.ActionType, SpecialVariables.ActionTypes.TentaclePackage, StringComparison.OrdinalIgnoreCase))
            return ExecutionScope.StepLevel;

        // An unregistered action must not be silently reclassified as server-only.
        // Target-level is conservative: the pipeline will expose the missing handler
        // as a configuration error instead of reporting a successful no-op deploy.
        return ExecutionScope.TargetLevel;
    }
}
