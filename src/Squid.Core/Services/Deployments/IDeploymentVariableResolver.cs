using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public interface IDeploymentVariableResolver : IScopedDependency
{
    Task<List<VariableDto>> ResolveVariablesAsync(int deploymentId, CancellationToken cancellationToken);
}
