using System.Threading.Tasks;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public interface IDeploymentVariableResolver : IScopedDependency
{
    Task<VariableSetSnapshotData> ResolveVariablesAsync(int deploymentId, CancellationToken cancellationToken);
}
