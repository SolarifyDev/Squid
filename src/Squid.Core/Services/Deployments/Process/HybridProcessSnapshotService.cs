using Microsoft.Extensions.Logging;
using Squid.Core.DependencyInjection;
using Squid.Core.Persistence;
using Squid.Core.Services.Common;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments.Process;

public interface IHybridProcessSnapshotService : IScopedDependency
{
    Task<int> CreateSnapshotAsync(int processId, string createdBy, CancellationToken cancellationToken = default);

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

    public async Task<int> CreateSnapshotAsync(int processId, string createdBy, CancellationToken cancellationToken = default)
    {
        return await _genericDataProvider.ExecuteInTransactionAsync<int>(
            async token =>
            {
                var process = await _processDataProvider.GetDeploymentProcessByIdAsync(processId, token).ConfigureAwait(false);

                if (process == null)
                    throw new Exception($"DeploymentProcess {processId} not found");

                var steps = await _stepDataProvider.GetDeploymentStepsByProcessIdAsync(processId, token).ConfigureAwait(false);

                var hashData = new
                {
                    process.Name,
                    process.ProjectId,
                    process.Version,
                    process.IsFrozen,
                    Steps = steps
                        .OrderBy(s => s.StepOrder)
                        .Select(
                            s => new
                            {
                                s.Name, s.StepOrder, s.StepType
                            })
                        .ToList()
                };

                var json = System.Text.Json.JsonSerializer.Serialize(hashData);

                var currentHash = UtilService.ComputeSha256Hash(json);

                var existingSnapshot = await _snapshotDataProvider.GetExistingSnapshotAsync(processId, currentHash, token).ConfigureAwait(false);

                if (existingSnapshot != null)
                {
                    return existingSnapshot.Id;
                }

                var snapshotData = await LoadCompleteProcessAsync(processId, token).ConfigureAwait(false);

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

                return snapshot.Id;
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

        var processSnapshots = new List<ProcessDetailSnapshotData>();

        foreach (var step in steps.OrderBy(s => s.StepOrder))
        {
            var stepProperties = await _stepPropertyDataProvider.GetDeploymentStepPropertiesByStepIdAsync(step.Id, cancellationToken).ConfigureAwait(false);

            var actions = await _actionDataProvider.GetDeploymentActionsByStepIdAsync(step.Id, cancellationToken).ConfigureAwait(false);

            var actionSnapshots = new List<ActionSnapshotData>();

            foreach (var action in actions.OrderBy(a => a.ActionOrder))
            {
                var actionProperties = await _actionPropertyDataProvider.GetDeploymentActionPropertiesByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);

                var environments = await _actionEnvironmentDataProvider.GetActionEnvironmentsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
                var channels = await _actionChannelDataProvider.GetActionChannelsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
                var machineRoles = await _actionMachineRoleDataProvider.GetActionMachineRolesByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);

                var actionSnapshot = new ActionSnapshotData
                {
                    Id = action.Id,
                    Name = action.Name,
                    ActionType = action.ActionType,
                    ActionOrder = action.ActionOrder,
                    WorkerPoolId = action.WorkerPoolId,
                    IsDisabled = action.IsDisabled,
                    IsRequired = action.IsRequired,
                    CanBeUsedForProjectVersioning = action.CanBeUsedForProjectVersioning,
                    CreatedAt = action.CreatedAt,
                    Properties = actionProperties.ToDictionary(p => p.PropertyName, p => p.PropertyValue),
                    Environments = environments.Select(e => e.EnvironmentId).ToList(),
                    Channels = channels.Select(c => c.ChannelId).ToList(),
                    MachineRoles = machineRoles.Select(m => m.MachineRole).ToList()
                };

                actionSnapshots.Add(actionSnapshot);
            }

            var processDetailSnapshot = new ProcessDetailSnapshotData
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
            CreatedAt = process.CreatedAt,
            Processes = processSnapshots
        };
    }
}
