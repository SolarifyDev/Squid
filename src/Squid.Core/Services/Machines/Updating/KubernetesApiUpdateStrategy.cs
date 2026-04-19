using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

/// <summary>
/// Update strategy for <see cref="CommunicationStyle.KubernetesApi"/>
/// machines. Extracted verbatim from the old MachineService.ApplyKubernetesEndpointUpdate
/// and split into validate + apply (R6-F).
/// </summary>
public sealed class KubernetesApiUpdateStrategy : IMachineUpdateStrategy
{
    private static readonly IReadOnlySet<string> OwnedFields = new HashSet<string>
    {
        nameof(UpdateMachineCommand.ClusterUrl),
        nameof(UpdateMachineCommand.Namespace),
        nameof(UpdateMachineCommand.SkipTlsVerification),
        nameof(UpdateMachineCommand.ProviderType),
        nameof(UpdateMachineCommand.ProviderConfig),
        nameof(UpdateMachineCommand.ResourceReferences),
    };

    public bool CanHandle(string communicationStyle)
        => communicationStyle == nameof(CommunicationStyle.KubernetesApi);

    public void ValidateForStyle(int machineId, UpdateMachineCommand command)
        => CrossStyleContaminationGuard.ThrowIfCommandTouchesNonOwnedFields(
            machineId, nameof(CommunicationStyle.KubernetesApi), OwnedFields, command);

    public bool ApplyEndpointUpdate(Machine machine, UpdateMachineCommand c)
    {
        if (c.ClusterUrl == null && c.Namespace == null && !c.SkipTlsVerification.HasValue
            && !c.ProviderType.HasValue && c.ProviderConfig == null && c.ResourceReferences == null)
            return false;

        var endpoint = !string.IsNullOrEmpty(machine.Endpoint)
            ? JsonSerializer.Deserialize<KubernetesApiEndpointDto>(machine.Endpoint, SquidJsonDefaults.CaseInsensitive)
            : new KubernetesApiEndpointDto();

        if (c.ClusterUrl != null) endpoint.ClusterUrl = c.ClusterUrl;
        if (c.Namespace != null) endpoint.Namespace = c.Namespace;
        if (c.SkipTlsVerification.HasValue) endpoint.SkipTlsVerification = c.SkipTlsVerification.Value.ToString();
        if (c.ProviderType.HasValue) endpoint.ProviderType = c.ProviderType.Value;
        if (c.ProviderConfig != null) endpoint.ProviderConfig = c.ProviderConfig;
        if (c.ResourceReferences != null) endpoint.ResourceReferences = c.ResourceReferences;

        machine.Endpoint = JsonSerializer.Serialize(endpoint);
        return true;
    }
}
