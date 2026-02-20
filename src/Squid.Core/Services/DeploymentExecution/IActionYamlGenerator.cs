using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution;

public interface IActionYamlGenerator : IScopedDependency
{
    bool CanHandle(DeploymentActionDto action);

    Task<Dictionary<string, byte[]>> GenerateAsync(
        DeploymentStepDto step,
        DeploymentActionDto action,
        CancellationToken cancellationToken);
}

