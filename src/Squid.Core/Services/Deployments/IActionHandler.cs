using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments;

public interface IActionHandler : IScopedDependency
{
    string ActionType { get; }

    bool CanHandle(DeploymentActionDto action);

    Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct);
}
