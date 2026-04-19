using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Json;

namespace Squid.Core.Services.Machines.Updating;

/// <summary>
/// Template for every per-style <see cref="IMachineUpdateStrategy"/>. Handles
/// the three mechanical jobs every strategy used to duplicate (dispatch by
/// <c>CommunicationStyle</c>, cross-style contamination check, deserialise
/// → apply → re-serialise), leaving each concrete strategy with just three
/// declarations:
///
/// <list type="number">
///   <item><see cref="StyleName"/> — the CommunicationStyle string this strategy owns.</item>
///   <item><see cref="OwnedFieldNames"/> — the set of <see cref="UpdateMachineCommand"/>
///         property names this style is allowed to modify. Used both by
///         <c>CrossStyleContaminationGuard</c> (to reject foreign fields) and
///         by the base's <c>HasAnyOwnedFieldSet</c> check (to skip the
///         deserialise/re-serialise cycle when nothing in this style's
///         scope was set).</item>
///   <item><see cref="ApplyOwnedFields"/> — pure field-by-field merge,
///         idiomatically one <c>??</c> line per editable field.</item>
/// </list>
///
/// <para>Round-6 refactor: the first implementation had each strategy
/// repeat the 3-step outer shell AND a per-field <c>if (c.X != null)</c>
/// ladder — ~40 lines each, 6× = 240 lines of ceremony. This base class
/// + the <c>??</c> coalescence idiom collapses each concrete to ~20 lines
/// of pure data (style name + owned-field set + field-merge block).</para>
/// </summary>
public abstract class MachineUpdateStrategyBase<TEndpointDto> : IMachineUpdateStrategy
    where TEndpointDto : class, new()
{
    protected abstract string StyleName { get; }

    protected abstract IReadOnlySet<string> OwnedFieldNames { get; }

    /// <summary>
    /// Merge command-provided fields onto the endpoint DTO. Use the
    /// <c>endpoint.X = command.X ?? endpoint.X</c> idiom for reference and
    /// nullable-value types alike — one line per field, clean and homogeneous.
    /// </summary>
    protected abstract void ApplyOwnedFields(TEndpointDto endpoint, UpdateMachineCommand command);

    public bool CanHandle(string communicationStyle) => communicationStyle == StyleName;

    public void ValidateForStyle(int machineId, UpdateMachineCommand command)
        => CrossStyleContaminationGuard.ThrowIfCommandTouchesNonOwnedFields(
            machineId, StyleName, OwnedFieldNames, command);

    public bool ApplyEndpointUpdate(Machine machine, UpdateMachineCommand command)
    {
        if (!HasAnyOwnedFieldSet(command)) return false;

        var endpoint = DeserialiseOrNew(machine.Endpoint);

        ApplyOwnedFields(endpoint, command);

        machine.Endpoint = JsonSerializer.Serialize(endpoint);
        return true;
    }

    /// <summary>
    /// True iff the command has at least one field that this strategy
    /// owns. Lets us skip the deserialise/re-serialise cycle entirely when
    /// e.g. a Tentacle machine is just being renamed — no wasteful JSON
    /// round-trip and no risk of a bug in apply corrupting the endpoint.
    /// </summary>
    private bool HasAnyOwnedFieldSet(UpdateMachineCommand command)
        => UpdateCommandFieldInventory
            .EnumerateStyleFields(command)
            .Any(f => f.IsSet && OwnedFieldNames.Contains(f.Name));

    private static TEndpointDto DeserialiseOrNew(string endpointJson)
        => !string.IsNullOrEmpty(endpointJson)
            ? JsonSerializer.Deserialize<TEndpointDto>(endpointJson, SquidJsonDefaults.CaseInsensitive)
            : new TEndpointDto();
}
