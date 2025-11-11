using System.Collections.Generic;
using System.Threading.Tasks;
using Squid.Core.Services.Deployments.Deployment;
using Squid.Core.Services.Deployments.Machine;
using Squid.Core.Services.Deployments.Channel;
using Squid.Core.Services.Deployments.Environment;
using Squid.Message.Domain.Deployments;
namespace Squid.Core.Services.Deployments;

public class DeploymentTargetFinder : IDeploymentTargetFinder
{
    private readonly IDeploymentDataProvider _deploymentDataProvider;
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IChannelDataProvider _channelDataProvider;
    private readonly IEnvironmentDataProvider _environmentDataProvider;

    public DeploymentTargetFinder(
        IDeploymentDataProvider deploymentDataProvider,
        IMachineDataProvider machineDataProvider,
        IChannelDataProvider channelDataProvider,
        IEnvironmentDataProvider environmentDataProvider)
    {
        _deploymentDataProvider = deploymentDataProvider;
        _machineDataProvider = machineDataProvider;
        _channelDataProvider = channelDataProvider;
        _environmentDataProvider = environmentDataProvider;
    }

    public async Task<List<Squid.Message.Domain.Deployments.Machine>> FindTargetsAsync(int deploymentId)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByIdAsync(deploymentId).ConfigureAwait(false);

        if (deployment == null)
        {
            throw new System.InvalidOperationException($"Deployment {deploymentId} not found.");
        }

        Log.Information("Finding target machines for deployment {DeploymentId} in environment {EnvironmentId}",
            deploymentId, deployment.EnvironmentId);

        // 基于部署的环境筛选目标机器
        var targetEnvironmentIds = new HashSet<int> { deployment.EnvironmentId };
        var targetMachineRoles = new HashSet<string>(); // 暂时不使用角色筛选

        // 筛选机器
        var machines = await _machineDataProvider.GetMachinesByFilterAsync(targetEnvironmentIds, targetMachineRoles).ConfigureAwait(false);

        Log.Information("Found {MachineCount} target machines for deployment {DeploymentId}", machines.Count, deploymentId);

        return machines;
    }
}
