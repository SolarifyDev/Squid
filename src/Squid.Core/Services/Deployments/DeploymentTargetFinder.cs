using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Message.Domain.Deployments;
namespace Squid.Core.Services.Deployments;

public class DeploymentTargetFinder : IDeploymentTargetFinder
{
    private readonly SquidDbContext _dbContext;

    public DeploymentTargetFinder(SquidDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<Squid.Message.Domain.Deployments.Machine>> FindTargetsAsync(int deploymentId)
    {
        var deployment = await _dbContext.Set<Deployment>().FindAsync(deploymentId);

        if (deployment == null)
        {
            throw new System.InvalidOperationException($"Deployment {deploymentId} not found.");
        }

        // 以环境为例筛选目标机器，后续可扩展角色/租户等
        var envId = deployment.EnvironmentId.ToString();

        var machines = await _dbContext.Set<Squid.Message.Domain.Deployments.Machine>()
            .Where(m => !m.IsDisabled && m.EnvironmentIds.Contains(envId))
            .ToListAsync();

        // 可扩展：按角色、租户等进一步筛选
        return machines;
    }
}
