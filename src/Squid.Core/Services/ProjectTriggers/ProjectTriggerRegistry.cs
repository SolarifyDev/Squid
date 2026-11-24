using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers;

public sealed class ProjectTriggerRegistry : IProjectTriggerRegistry
{
    private readonly Dictionary<ProjectTriggerCapability, IProjectTriggerHandler> _handlers = new();

    public ProjectTriggerRegistry(IEnumerable<IProjectTriggerHandler> handlers)
    {
        foreach (var handler in handlers)
            RegisterHandler(handler);

        Capabilities = _handlers.Keys.OrderBy(key => key.TriggerType, StringComparer.Ordinal)
            .ThenBy(key => key.ActionType, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<ProjectTriggerCapability> Capabilities { get; }

    public IProjectTriggerHandler Resolve(string triggerType, string actionType)
    {
        if (_handlers.TryGetValue(new ProjectTriggerCapability(triggerType, actionType), out var handler))
            return handler;

        throw new ArgumentException("This trigger type and action are not available.");
    }

    private void RegisterHandler(IProjectTriggerHandler handler)
    {
        foreach (var action in handler.ActionTypes)
        {
            var capability = new ProjectTriggerCapability(handler.TriggerType, action);

            if (!IsSupported(capability))
                throw new InvalidOperationException($"Unsupported trigger/action registration: {capability}.");

            if (!_handlers.TryAdd(capability, handler))
                throw new InvalidOperationException($"Duplicate trigger/action registration: {capability}.");
        }
    }

    private static bool IsSupported(ProjectTriggerCapability capability)
    {
        return capability switch
        {
            (ProjectTriggerTypes.GitRepository or ProjectTriggerTypes.ExternalFeed,
                ProjectTriggerActions.CreateRelease) => true,
            (ProjectTriggerTypes.DeploymentTarget, ProjectTriggerActions.AutoDeploy) => true,
            (ProjectTriggerTypes.Scheduled,
                ProjectTriggerActions.DeployLatestRelease or ProjectTriggerActions.DeployNewRelease
                    or ProjectTriggerActions.DeployLatestReleaseToEnvironment) => true,
            _ => false
        };
    }
}
