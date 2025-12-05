using Squid.Core.Services.Deployments.Channel;
using Squid.Core.Services.Deployments.Deployment;
using Squid.Core.Services.Deployments.Environment;
using Squid.Core.Services.Deployments.Machine;

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

    public async Task<Message.Domain.Deployments.Machine> FindTargetsAsync(Message.Domain.Deployments.Deployment deployment, CancellationToken cancellationToken)
    {
        Log.Information("Finding target machines for deployment {@Deployment} in environment {EnvironmentId}",
            deployment, deployment.EnvironmentId);

        // 基于部署的环境筛选目标机器
        // var targetEnvironmentIds = new HashSet<int> { deployment.EnvironmentId };
        // var targetMachineRoles = new HashSet<string>(); // 暂时不使用角色筛选

        // 筛选机器
        var machine = await _machineDataProvider.GetMachinesByIdAsync(deployment.MachineId, cancellationToken).ConfigureAwait(false);

        Log.Information("Found {@Machine} target machines for deployment {Deployment}", machine, deployment);

        return machine;
    }
}
