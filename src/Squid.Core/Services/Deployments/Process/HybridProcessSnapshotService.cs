using System.Text;
using Newtonsoft.Json;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Process.Action;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.Deployments.Process;

public interface IHybridProcessSnapshotService : IScopedDependency
{
    Task<ProcessSnapshotData> GetOrCreateSnapshotAsync(int processId, string createdBy, CancellationToken cancellationToken = default);

    Task<ProcessSnapshotData> LoadSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default);

    Task<List<ProcessSnapshotData>> LoadSnapshotsAsync(List<int> snapshotIds, CancellationToken cancellationToken = default);
}

public class HybridProcessSnapshotService : IHybridProcessSnapshotService
{
    private readonly IMapper _mapper;
    private readonly IGenericDataProvider _genericDataProvider;
    private readonly IProcessSnapshotDataProvider _snapshotDataProvider;
    private readonly IDeploymentProcessDataProvider _processDataProvider;
    private readonly IDeploymentStepDataProvider _stepDataProvider;
    private readonly IDeploymentStepPropertyDataProvider _stepPropertyDataProvider;
    private readonly IDeploymentActionDataProvider _actionDataProvider;
    private readonly IDeploymentActionPropertyDataProvider _actionPropertyDataProvider;
    private readonly IActionEnvironmentDataProvider _actionEnvironmentDataProvider;
    private readonly IActionChannelDataProvider _actionChannelDataProvider;
    private readonly IActionMachineRoleDataProvider _actionMachineRoleDataProvider;

    public HybridProcessSnapshotService(
        IMapper mapper,
        IGenericDataProvider genericDataProvider,
        IProcessSnapshotDataProvider snapshotDataProvider,
        IDeploymentProcessDataProvider processDataProvider,
        IDeploymentStepDataProvider stepDataProvider,
        IDeploymentStepPropertyDataProvider stepPropertyDataProvider,
        IDeploymentActionDataProvider actionDataProvider,
        IDeploymentActionPropertyDataProvider actionPropertyDataProvider,
        IActionEnvironmentDataProvider actionEnvironmentDataProvider,
        IActionChannelDataProvider actionChannelDataProvider,
        IActionMachineRoleDataProvider actionMachineRoleDataProvider)
    {
        _mapper = mapper;
        _genericDataProvider = genericDataProvider;
        _snapshotDataProvider = snapshotDataProvider;
        _processDataProvider = processDataProvider;
        _stepDataProvider = stepDataProvider;
        _stepPropertyDataProvider = stepPropertyDataProvider;
        _actionDataProvider = actionDataProvider;
        _actionPropertyDataProvider = actionPropertyDataProvider;
        _actionEnvironmentDataProvider = actionEnvironmentDataProvider;
        _actionChannelDataProvider = actionChannelDataProvider;
        _actionMachineRoleDataProvider = actionMachineRoleDataProvider;
    }

    public async Task<ProcessSnapshotData> GetOrCreateSnapshotAsync(int processId, string createdBy, CancellationToken cancellationToken = default)
    {
        return await _genericDataProvider.ExecuteInTransactionAsync(
            async token =>
            {
                var process = await _processDataProvider.GetDeploymentProcessByIdAsync(processId, token).ConfigureAwait(false);

                if (process == null) throw new Exception($"DeploymentProcess {processId} not found");
                
                var snapshotData = await LoadCompleteProcessAsync(processId, token).ConfigureAwait(false);

                var json = JsonConvert.SerializeObject(snapshotData);

                var currentHash = UtilService.ComputeSha256Hash(json);

                var existingSnapshot = await _snapshotDataProvider.GetExistingSnapshotAsync(processId, currentHash, token).ConfigureAwait(false);

                if (existingSnapshot != null) return UtilService.DecompressFromGzip<ProcessSnapshotData>(existingSnapshot.SnapshotData);

                var compressedData = UtilService.CompressToGzip(snapshotData);

                var uncompressedSize = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(snapshotData));

                var snapshot = new ProcessSnapshot
                {
                    OriginalProcessId = processId,
                    Version = snapshotData.Version,
                    SnapshotData = compressedData,
                    ContentHash = currentHash,
                    CompressionType = "GZIP",
                    UncompressedSize = uncompressedSize,
                    CreatedBy = createdBy
                };

                await _snapshotDataProvider.AddProcessSnapshotAsync(snapshot, false, token).ConfigureAwait(false);
                
                Log.Information(
                    "Snapshot {SnapshotId} created successfully. " +
                    "Compressed size: {CompressedSize} bytes, " +
                    "Uncompressed size: {UncompressedSize} bytes, " +
                    "Compression ratio: {Ratio:P2}",
                    snapshot.Id, compressedData.Length, uncompressedSize,
                    1.0 - (double)compressedData.Length / uncompressedSize);

                return snapshotData;
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProcessSnapshotData> LoadSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _snapshotDataProvider.GetProcessSnapshotByIdAsync(snapshotId, cancellationToken).ConfigureAwait(false);

        if (snapshot == null)
            throw new Exception($"Snapshot {snapshotId} not found");

        var snapshotData = UtilService.DecompressFromGzip<ProcessSnapshotData>(snapshot.SnapshotData);

        return snapshotData;
    }

    public async Task<List<ProcessSnapshotData>> LoadSnapshotsAsync(List<int> snapshotIds, CancellationToken cancellationToken = default)
    {
        var snapshots = await _snapshotDataProvider.GetSnapshotsAsync(snapshotIds, cancellationToken).ConfigureAwait(false);

        if (snapshots.Count == 0)
            throw new Exception($"No snapshots found for {string.Join(',', snapshotIds)} processes");

        var snapshotData = snapshots.ConvertAll(x =>
        {
            var data = UtilService.DecompressFromGzip<ProcessSnapshotData>(x.SnapshotData);
            return data;
        });

        return snapshotData;
    }

    private async Task<ProcessSnapshotData> LoadCompleteProcessAsync(int processId, CancellationToken cancellationToken)
    {
        var process = await _processDataProvider.GetDeploymentProcessByIdAsync(processId, cancellationToken).ConfigureAwait(false);

        if (process == null)
            throw new Exception($"DeploymentProcess {processId} not found");

        var steps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(processId, cancellationToken).ConfigureAwait(false);
        var stepIds = steps.Select(s => s.Id).ToList();

        var allActions = await LoadAllActionsByStepIds(stepIds, cancellationToken);
        var actionIds = allActions.Select(a => a.Id).ToList();

        var actionPropertiesDict = await LoadActionPropertiesDict(actionIds, cancellationToken);
        var environmentsDict = await LoadEnvironmentsDict(actionIds, cancellationToken);
        var channelsDict = await LoadChannelsDict(actionIds, cancellationToken);
        var machineRolesDict = await LoadMachineRolesDict(actionIds, cancellationToken);
        var stepPropertiesDict = await LoadStepPropertiesDict(stepIds, cancellationToken);

        var processSnapshots = new List<StepSnapshotData>();

        foreach (var step in steps.OrderBy(s => s.StepOrder))
        {
            var stepProperties = stepPropertiesDict.TryGetValue(step.Id, out var sps) ? sps : new List<DeploymentStepProperty>();

            var actions = allActions.Where(a => a.StepId == step.Id).OrderBy(a => a.ActionOrder).ToList();

            var actionSnapshots = new List<ActionSnapshotData>();

            foreach (var action in actions)
            {
                var actionProperties = actionPropertiesDict.TryGetValue(action.Id, out var aps) ? aps : new List<DeploymentActionProperty>();
                var environments = environmentsDict.TryGetValue(action.Id, out var envs) ? envs : new List<int>();
                var channels = channelsDict.TryGetValue(action.Id, out var chs) ? chs : new List<int>();
                var machineRoles = machineRolesDict.TryGetValue(action.Id, out var mrs) ? mrs : new List<string>();

                var actionSnapshot = new ActionSnapshotData
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
                    CanBeUsedForProjectVersioning = action.CanBeUsedForProjectVersioning,
                    CreatedAt = action.CreatedAt,
                    Properties = actionProperties.ToDictionary(p => p.PropertyName, p => p.PropertyValue),
                    Environments = environments,
                    Channels = channels,
                    MachineRoles = machineRoles
                };

                actionSnapshots.Add(actionSnapshot);
            }

            var processDetailSnapshot = new StepSnapshotData
            {
                Id = step.Id,
                Name = step.Name,
                StepType = step.StepType,
                StepOrder = step.StepOrder,
                Condition = step.Condition,
                Properties = stepProperties.ToDictionary(p => p.PropertyName, p => p.PropertyValue),
                CreatedAt = step.CreatedAt,
                Actions = actionSnapshots
            };

            processSnapshots.Add(processDetailSnapshot);
        }

        return new ProcessSnapshotData
        {
            Id = processId,
            Version = process.Version,
            CreatedAt = process.LastModified,
            StepSnapshots = processSnapshots
        };
    }

    private async Task<List<DeploymentAction>> LoadAllActionsByStepIds(List<int> stepIds, CancellationToken cancellationToken)
    {
        return await _actionDataProvider.GetDeploymentActionsByStepIdsAsync(stepIds, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, List<DeploymentActionProperty>>> LoadActionPropertiesDict(List<int> actionIds, CancellationToken cancellationToken)
    {
        var allActionProperties = await _actionPropertyDataProvider.GetDeploymentActionPropertiesByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);
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

    private async Task<Dictionary<int, List<DeploymentStepProperty>>> LoadStepPropertiesDict(List<int> stepIds, CancellationToken cancellationToken)
    {
        var allStepProperties = await _stepPropertyDataProvider.GetDeploymentStepPropertiesByStepIdsAsync(stepIds, cancellationToken).ConfigureAwait(false);
        return allStepProperties.GroupBy(p => p.StepId).ToDictionary(g => g.Key, g => g.ToList());
    }
}
