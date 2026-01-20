using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Process.Action;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Variables;

namespace Squid.Core.Services.Deployments.Snapshots;

public partial interface IDeploymentSnapshotService : IScopedDependency
{
}

public partial class DeploymentSnapshotService : IDeploymentSnapshotService
{
    private readonly IMapper _mapper;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IVariableDataProvider _variableDataProvider;
    private readonly IDeploymentSnapshotDataProvider _deploymentSnapshotDataProvider;
    private readonly IDeploymentProcessDataProvider _deploymentProcessDataProvider;
    private readonly IDeploymentStepDataProvider _deploymentStepDataProvider;
    private readonly IDeploymentActionDataProvider _deploymentActionDataProvider;
    private readonly IDeploymentActionPropertyDataProvider _deploymentActionPropertyDataProvider;
    private readonly IDeploymentStepPropertyDataProvider _deploymentStepPropertyDataProvider;
    private readonly IActionChannelDataProvider _actionChannelDataProvider;
    private readonly IActionEnvironmentDataProvider _actionEnvironmentDataProvider;
    private readonly IActionMachineRoleDataProvider _actionMachineRoleDataProvider;
    
    public DeploymentSnapshotService(
        IMapper mapper, 
        IProjectDataProvider projectDataProvider, 
        IVariableDataProvider variableDataProvider, 
        IDeploymentSnapshotDataProvider deploymentSnapshotDataProvider, 
        IDeploymentProcessDataProvider deploymentProcessDataProvider, 
        IDeploymentStepDataProvider deploymentStepDataProvider, 
        IDeploymentActionDataProvider deploymentActionDataProvider, 
        IDeploymentActionPropertyDataProvider deploymentActionPropertyDataProvider, 
        IDeploymentStepPropertyDataProvider deploymentStepPropertyDataProvider,
        IActionChannelDataProvider actionChannelDataProvider, 
        IActionEnvironmentDataProvider actionEnvironmentDataProvider, 
        IActionMachineRoleDataProvider actionMachineRoleDataProvider)
    {
        _mapper = mapper;
        _projectDataProvider = projectDataProvider;
        _variableDataProvider = variableDataProvider;
        _deploymentSnapshotDataProvider = deploymentSnapshotDataProvider;
        _deploymentProcessDataProvider = deploymentProcessDataProvider;
        _deploymentStepDataProvider = deploymentStepDataProvider;
        _deploymentActionDataProvider = deploymentActionDataProvider;
        _deploymentActionPropertyDataProvider = deploymentActionPropertyDataProvider;
        _deploymentStepPropertyDataProvider = deploymentStepPropertyDataProvider;
        _actionChannelDataProvider = actionChannelDataProvider;
        _actionEnvironmentDataProvider = actionEnvironmentDataProvider;
        _actionMachineRoleDataProvider = actionMachineRoleDataProvider;
    }
}