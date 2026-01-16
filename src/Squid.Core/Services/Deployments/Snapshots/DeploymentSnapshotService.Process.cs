using System.Text;
using Newtonsoft.Json;
using Squid.Core.Services.Common;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.Core.Services.Deployments.Snapshots;

public partial interface IDeploymentSnapshotService
{
    Task<DeploymentProcessSnapshot> SnapshotProcessFromReleaseAsync(Persistence.Entities.Deployments.Release release, CancellationToken cancellationToken = default);

    Task<DeploymentProcessSnapshotDto> LoadProcessSnapshotAsync(int processSnapshotId, CancellationToken cancellationToken = default);
}

public partial class DeploymentSnapshotService
{
    public async Task<DeploymentProcessSnapshot> SnapshotProcessFromReleaseAsync(Persistence.Entities.Deployments.Release release, CancellationToken cancellationToken = default)
    {
        var project = await _projectDataProvider.GetProjectByIdAsync(release.ProjectId, cancellationToken).ConfigureAwait(false);
        
        var process = await _deploymentProcessDataProvider.GetDeploymentProcessByIdAsync(project.DeploymentProcessId, cancellationToken).ConfigureAwait(false);
        
        var snapshotData = await GenerateProcessSnapshotData(process, cancellationToken).ConfigureAwait(false);

        var processSnapshot = BuildProcessSnapshot(process, snapshotData);

        await _deploymentSnapshotDataProvider.AddDeploymentProcessSnapshotAsync(processSnapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        return processSnapshot;
    }

    public async Task<DeploymentProcessSnapshotDto> LoadProcessSnapshotAsync(int processSnapshotId, CancellationToken cancellationToken = default)
    {
        var snapshotFromDb = await _deploymentSnapshotDataProvider.GetDeploymentProcessSnapshotByIdAsync(processSnapshotId, cancellationToken).ConfigureAwait(false);

        if (snapshotFromDb == null) throw new ArgumentNullException(nameof(snapshotFromDb));

        var snapshot = _mapper.Map<DeploymentProcessSnapshotDto>(snapshotFromDb);
        
        snapshot.Data = UtilService.DecompressFromGzip<DeploymentProcessSnapshotDataDto>(snapshotFromDb.SnapshotData);

        return snapshot;
    }
    
    private DeploymentProcessSnapshot BuildProcessSnapshot(DeploymentProcess process, DeploymentProcessSnapshotDataDto snapshotData)
    {
        var compressedData = UtilService.CompressToGzip(snapshotData);
        var uncompressedSize = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(snapshotData));
        var contentHash = UtilService.ComputeSha256Hash(JsonConvert.SerializeObject(snapshotData));
        
        return new DeploymentProcessSnapshot
        {
            Version = process.Version,
            OriginalProcessId =  process.Id,
            CreatedBy = "System",
            CreatedAt = process.LastModified,
            ContentHash = contentHash,
            SnapshotData = compressedData,
            UncompressedSize = uncompressedSize,
            CompressionType = "GZIP"
        };
    }
    
    private async Task<DeploymentProcessSnapshotDataDto> GenerateProcessSnapshotData(DeploymentProcess process, CancellationToken cancellationToken)
    {
        var snapshotData = new DeploymentProcessSnapshotDataDto();
        
        var steps = await _deploymentStepDataProvider.GetDeploymentStepsByProcessIdAsync(process.Id, cancellationToken).ConfigureAwait(false);
        var stepIds = steps.Select(s => s.Id).ToList();

        var allActions = await LoadAllActionsByStepIds(stepIds, cancellationToken);
        var actionIds = allActions.Select(a => a.Id).ToList();

        var actionPropertiesDict = await LoadActionPropertiesDict(actionIds, cancellationToken);
        var environmentsDict = await LoadEnvironmentsDict(actionIds, cancellationToken);
        var channelsDict = await LoadChannelsDict(actionIds, cancellationToken);
        var machineRolesDict = await LoadMachineRolesDict(actionIds, cancellationToken);
        var stepPropertiesDict = await LoadStepPropertiesDict(stepIds, cancellationToken);
        
        foreach (var step in steps.OrderBy(s => s.StepOrder))
        {
            var stepProperties = stepPropertiesDict.TryGetValue(step.Id, out var sps) ? sps : new List<DeploymentStepProperty>();

            var actions = allActions.Where(a => a.StepId == step.Id).OrderBy(a => a.ActionOrder).ToList();

            var actionSnapshots = new List<DeploymentActionSnapshotDataDto>();

            foreach (var action in actions)
            {
                var actionProperties = actionPropertiesDict.TryGetValue(action.Id, out var aps) ? aps : new List<DeploymentActionProperty>();
                var environments = environmentsDict.TryGetValue(action.Id, out var envs) ? envs : new List<int>();
                var channels = channelsDict.TryGetValue(action.Id, out var chs) ? chs : new List<int>();
                var machineRoles = machineRolesDict.TryGetValue(action.Id, out var mrs) ? mrs : new List<string>();

                var actionSnapshot = new DeploymentActionSnapshotDataDto
                {
                    Id = action.Id,
                    Name = action.Name,
                    ActionType = action.ActionType,
                    ActionOrder = action.ActionOrder,
                    WorkerPoolId = action.WorkerPoolId,
                    FeedId = action.FeedId,
                    PackageId = action.PackageId,
                    IsDisabled = action.IsDisabled,
                    IsRequired = action.IsRequired,
                    CreatedAt = action.CreatedAt,
                    Environments = environments,
                    Channels = channels,
                    MachineRoles = machineRoles,
                    CanBeUsedForProjectVersioning = action.CanBeUsedForProjectVersioning,
                    Properties = actionProperties.ToDictionary(p => p.PropertyName, p => p.PropertyValue)
                };

                actionSnapshots.Add(actionSnapshot);
            }

            var processDetailSnapshot = new DeploymentStepSnapshotDataDto
            {
                Id = step.Id,
                Name = step.Name,
                StepType = step.StepType,
                StepOrder = step.StepOrder,
                Condition = step.Condition,
                CreatedAt = step.CreatedAt,
                ActionSnapshots = actionSnapshots,
                Properties = stepProperties.ToDictionary(p => p.PropertyName, p => p.PropertyValue)
            };

            snapshotData.StepSnapshots.Add(processDetailSnapshot);
        }

        return snapshotData;
    }
    
    
    private async Task<Dictionary<int, List<DeploymentStepProperty>>> LoadStepPropertiesDict(List<int> stepIds, CancellationToken cancellationToken)
    {
        var allStepProperties = await _deploymentStepPropertyDataProvider.GetDeploymentStepPropertiesByStepIdsAsync(stepIds, cancellationToken).ConfigureAwait(false);
        return allStepProperties.GroupBy(p => p.StepId).ToDictionary(g => g.Key, g => g.ToList());
    }
    
    private async Task<List<DeploymentAction>> LoadAllActionsByStepIds(List<int> stepIds, CancellationToken cancellationToken)
    {
        return await _deploymentActionDataProvider.GetDeploymentActionsByStepIdsAsync(stepIds, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, List<DeploymentActionProperty>>> LoadActionPropertiesDict(List<int> actionIds, CancellationToken cancellationToken)
    {
        var allActionProperties = await _deploymentActionPropertyDataProvider.GetDeploymentActionPropertiesByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);
        return allActionProperties.GroupBy(p => p.ActionId).ToDictionary(g => g.Key, g => g.ToList());
    }
    
    private async Task<Dictionary<int, List<int>>> LoadEnvironmentsDict(List<int> actionIds, CancellationToken cancellationToken)
    {
        var allEnvironments = await _actionEnvironmentDataProvider.GetActionEnvironmentsByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);
        return allEnvironments.GroupBy(e => e.ActionId).ToDictionary(g => g.Key, g => g.Select(e => e.EnvironmentId).ToList());
    }

    private async Task<Dictionary<int, List<int>>> LoadChannelsDict(List<int> actionIds, CancellationToken cancellationToken)
    {
        var allChannels = await _actionChannelDataProvider.GetActionChannelsByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);
        return allChannels.GroupBy(c => c.ActionId).ToDictionary(g => g.Key, g => g.Select(c => c.ChannelId).ToList());
    }

    private async Task<Dictionary<int, List<string>>> LoadMachineRolesDict(List<int> actionIds, CancellationToken cancellationToken)
    {
        var allMachineRoles = await _actionMachineRoleDataProvider.GetActionMachineRolesByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);
        return allMachineRoles.GroupBy(m => m.ActionId).ToDictionary(g => g.Key, g => g.Select(m => m.MachineRole).ToList());
    }
}