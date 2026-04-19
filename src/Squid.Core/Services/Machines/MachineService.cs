using System.Text.Json;
using Squid.Core.Halibut;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.Exceptions;
using Squid.Core.Services.Machines.Updating;
using Squid.Message.Commands.Machine;
using Squid.Message.Events.Machine;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Services.Machines;

public interface IMachineService : IScopedDependency
{
    Task<GetMachinesResponse> GetMachinesAsync(GetMachinesRequest request, CancellationToken cancellationToken);

    Task<UpdateMachineResponse> UpdateMachineAsync(UpdateMachineCommand command, CancellationToken cancellationToken);

    Task<MachineDeletedEvent> DeleteMachinesAsync(DeleteMachinesCommand command, CancellationToken cancellationToken);
}

public class MachineService : IMachineService
{
    private readonly IMapper _mapper;
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IPollingTrustDistributor _trustDistributor;
    private readonly IEnumerable<IMachineUpdateStrategy> _updateStrategies;

    public MachineService(
        IMapper mapper,
        IMachineDataProvider machineDataProvider,
        IPollingTrustDistributor trustDistributor,
        IEnumerable<IMachineUpdateStrategy> updateStrategies)
    {
        _mapper = mapper;
        _machineDataProvider = machineDataProvider;
        _trustDistributor = trustDistributor;
        _updateStrategies = updateStrategies;
    }

    public async Task<GetMachinesResponse> GetMachinesAsync(GetMachinesRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _machineDataProvider.GetMachinePagingAsync(
            request.SpaceId, request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetMachinesResponse
        {
            Data = new GetMachinesResponseData
            {
                Count = count,
                Machines = _mapper.Map<List<MachineDto>>(data)
            }
        };
    }

    public async Task<UpdateMachineResponse> UpdateMachineAsync(UpdateMachineCommand command, CancellationToken cancellationToken)
    {
        var machine = await _machineDataProvider.GetMachinesByIdAsync(command.MachineId, cancellationToken).ConfigureAwait(false);

        if (machine == null)
            throw new MachineNotFoundException(command.MachineId);

        await EnsureNameAvailableIfChangedAsync(machine, command, cancellationToken).ConfigureAwait(false);

        // Per-style endpoint update (Round-6 R6-F):
        //   1. Validate FIRST (throws before any mutation) — the old
        //      `ApplyKubernetesEndpointUpdate` corrupted Tentacle/Ssh/K8sAgent
        //      endpoint JSON by deserialising them as K8sApi.
        //   2. Apply common fields (Name / IsDisabled / Roles / ...)
        //   3. Dispatch endpoint update to the matching strategy; non-matching
        //      styles with no style-specific fields are a pure no-op.
        ValidateCommandAgainstMachineStyle(machine, command);

        ApplyCommonFields(machine, command);

        ApplyEndpointUpdate(machine, command);

        await _machineDataProvider.UpdateMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

        _trustDistributor.Reconfigure();

        Log.Information("Updated machine {MachineName} (Id={MachineId})", machine.Name, machine.Id);

        return new UpdateMachineResponse { Data = _mapper.Map<MachineDto>(machine) };
    }

    private async Task EnsureNameAvailableIfChangedAsync(Persistence.Entities.Deployments.Machine machine, UpdateMachineCommand command, CancellationToken ct)
    {
        if (command.Name == null || string.Equals(command.Name, machine.Name, StringComparison.Ordinal)) return;

        var spaceId = command.SpaceId ?? machine.SpaceId;

        if (await _machineDataProvider.ExistsByNameAsync(command.Name, spaceId, ct).ConfigureAwait(false))
            throw new MachineNameConflictException(command.Name, spaceId);
    }

    /// <summary>
    /// Validates the command's style-specific fields against the machine's
    /// actual CommunicationStyle. MUST happen BEFORE any mutation so a
    /// reject doesn't leave the entity partially modified. If no strategy
    /// handles the style, any style-specific field is still rejected by
    /// the inventory check via a best-guess style name ("Unknown").
    /// </summary>
    private void ValidateCommandAgainstMachineStyle(Persistence.Entities.Deployments.Machine machine, UpdateMachineCommand command)
    {
        var styleName = CommunicationStyleParser.Parse(machine.Endpoint).ToString();
        var strategy = _updateStrategies.FirstOrDefault(s => s.CanHandle(styleName));

        if (strategy != null)
        {
            strategy.ValidateForStyle(machine.Id, command);
            return;
        }

        // No strategy for this style (Unknown / None / newly-added style
        // without a strategy yet). Any style-specific field set on the
        // command is ambiguous at best — fail loudly instead of the old
        // "treat it as K8s" behaviour that silently corrupted Tentacle
        // endpoint JSON.
        var anyStyleSpecificFieldSet = UpdateCommandFieldInventory.EnumerateStyleFields(command).Any(f => f.IsSet);

        if (anyStyleSpecificFieldSet)
            throw new MachineEndpointUpdateNotApplicableException(
                machineId: machine.Id,
                machineStyle: string.IsNullOrEmpty(styleName) ? "Unknown" : styleName,
                offendingField: "<any style-specific field>",
                acceptedForStyles: "machine has no registered update strategy — verify CommunicationStyle in endpoint JSON");
    }

    private static void ApplyCommonFields(Persistence.Entities.Deployments.Machine machine, UpdateMachineCommand command)
    {
        if (command.Name != null)
            machine.Name = command.Name;

        if (command.IsDisabled.HasValue)
            machine.IsDisabled = command.IsDisabled.Value;

        if (command.Roles != null)
            machine.Roles = JsonSerializer.Serialize(command.Roles);

        if (command.EnvironmentIds != null)
            machine.EnvironmentIds = JsonSerializer.Serialize(command.EnvironmentIds);

        if (command.MachinePolicyId.HasValue)
            machine.MachinePolicyId = command.MachinePolicyId.Value;
    }

    private void ApplyEndpointUpdate(Persistence.Entities.Deployments.Machine machine, UpdateMachineCommand command)
    {
        var styleName = CommunicationStyleParser.Parse(machine.Endpoint).ToString();
        var strategy = _updateStrategies.FirstOrDefault(s => s.CanHandle(styleName));

        // Validation above already proved: strategy==null ⇒ no style-specific
        // fields on command. So it's safe to simply no-op here.
        strategy?.ApplyEndpointUpdate(machine, command);
    }

    public async Task<MachineDeletedEvent> DeleteMachinesAsync(DeleteMachinesCommand command, CancellationToken cancellationToken)
    {
        var machines = await _machineDataProvider.GetMachinesByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await _machineDataProvider.DeleteMachinesAsync(machines, cancellationToken: cancellationToken).ConfigureAwait(false);

        _trustDistributor.Reconfigure();

        var deletedIds = machines.Select(m => m.Id).ToList();
        var failIds = command.Ids.Except(deletedIds).ToList();

        Log.Information("Deleted {Count} machines: {Ids}", deletedIds.Count, deletedIds);

        return new MachineDeletedEvent
        {
            Data = new DeleteMachinesResponseData
            {
                FailIds = failIds
            }
        };
    }
}
