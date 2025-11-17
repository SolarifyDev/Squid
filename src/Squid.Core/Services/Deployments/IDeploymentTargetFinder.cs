using System.Collections.Generic;
using System.Threading.Tasks;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments;

public interface IDeploymentTargetFinder : IScopedDependency
{
    Task<List<Squid.Message.Domain.Deployments.Machine>> FindTargetsAsync(int deploymentId);
}
