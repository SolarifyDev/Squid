using System.Net;
using System.Text.Json;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines;

public partial class MachineScriptService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<GenerateKubernetesAgentUpgradeScriptResponse> GenerateKubernetesAgentUpgradeScriptAsync(GenerateKubernetesAgentUpgradeScriptCommand command, CancellationToken ct)
    {
        if (command == null)
            return Fail<GenerateKubernetesAgentUpgradeScriptResponse, GenerateKubernetesAgentUpgradeScriptData>(HttpStatusCode.BadRequest, "Command cannot be null");

        if (command.MachineId <= 0)
            return Fail<GenerateKubernetesAgentUpgradeScriptResponse, GenerateKubernetesAgentUpgradeScriptData>(HttpStatusCode.BadRequest, "MachineId must be greater than 0");

        try
        {
            var machine = await _machineDataProvider.GetMachinesByIdAsync(command.MachineId, ct).ConfigureAwait(false);

            if (machine == null)
            {
                return Fail<GenerateKubernetesAgentUpgradeScriptResponse, GenerateKubernetesAgentUpgradeScriptData>(HttpStatusCode.BadRequest, $"Machine {command.MachineId} not found");
            }

            if (!TryDeserializeEndpoint(machine.Endpoint, command.MachineId, out var endpoint, out var endpointError))
            {
                return Fail<GenerateKubernetesAgentUpgradeScriptResponse, GenerateKubernetesAgentUpgradeScriptData>(HttpStatusCode.Conflict, endpointError);
            }

            if (!IsKubernetesAgent(endpoint))
            {
                return Fail<GenerateKubernetesAgentUpgradeScriptResponse, GenerateKubernetesAgentUpgradeScriptData>(HttpStatusCode.BadRequest, $"Machine {command.MachineId} is not a KubernetesAgent target");
            }

            if (!TryValidateMetadata(endpoint, command.MachineId, out var metadataError))
            {
                return Fail<GenerateKubernetesAgentUpgradeScriptResponse, GenerateKubernetesAgentUpgradeScriptData>(HttpStatusCode.Conflict, metadataError);
            }

            var latestVersion = await _agentVersionProvider.GetLatestKubernetesAgentVersionAsync(ct).ConfigureAwait(false);
            
            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                return Fail<GenerateKubernetesAgentUpgradeScriptResponse, GenerateKubernetesAgentUpgradeScriptData>(HttpStatusCode.InternalServerError, "Failed to resolve latest Kubernetes Agent version");
            }

            if (!Version.TryParse(latestVersion, out _))
            {
                return Fail<GenerateKubernetesAgentUpgradeScriptResponse, GenerateKubernetesAgentUpgradeScriptData>(HttpStatusCode.InternalServerError, $"Latest Kubernetes Agent version '{latestVersion}' is invalid");
            }

            var data = new GenerateKubernetesAgentUpgradeScriptData
            {
                MachineId = machine.Id,
                CurrentVersion = machine.AgentVersion ?? string.Empty,
                LatestVersion = latestVersion,
                NeedsUpgrade = RequiresUpgrade(machine.AgentVersion, latestVersion),
                ReleaseName = endpoint.ReleaseName,
                HelmNamespace = endpoint.HelmNamespace,
                ChartRef = endpoint.ChartRef,
                UpgradeScript = BuildUpgradeScript(endpoint, latestVersion)
            };

            return Success<GenerateKubernetesAgentUpgradeScriptResponse, GenerateKubernetesAgentUpgradeScriptData>(data);
        }
        catch (Exception ex)
        {
            return Fail<GenerateKubernetesAgentUpgradeScriptResponse, GenerateKubernetesAgentUpgradeScriptData>(HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    private static bool TryDeserializeEndpoint(
        string endpointJson, int machineId, out KubernetesAgentEndpointDto endpoint, out string error)
    {
        endpoint = null;

        if (string.IsNullOrWhiteSpace(endpointJson))
        {
            error = $"KubernetesAgent metadata missing for machine {machineId}: endpoint is empty";
            return false;
        }

        try
        {
            endpoint = JsonSerializer.Deserialize<KubernetesAgentEndpointDto>(endpointJson, JsonOptions);
            
            if (endpoint == null)
            {
                error = $"KubernetesAgent metadata missing for machine {machineId}: endpoint cannot be deserialized";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException)
        {
            error = $"KubernetesAgent metadata missing for machine {machineId}: endpoint json is invalid";
            return false;
        }
    }

    private static bool IsKubernetesAgent(KubernetesAgentEndpointDto endpoint)
    {
        return Enum.TryParse<CommunicationStyle>(endpoint.CommunicationStyle, true, out var communicationStyle) && communicationStyle == CommunicationStyle.KubernetesAgent;
    }

    private static bool TryValidateMetadata(KubernetesAgentEndpointDto endpoint, int machineId, out string error)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(endpoint.ReleaseName))
            missing.Add(nameof(KubernetesAgentEndpointDto.ReleaseName));
        if (string.IsNullOrWhiteSpace(endpoint.HelmNamespace))
            missing.Add(nameof(KubernetesAgentEndpointDto.HelmNamespace));
        if (string.IsNullOrWhiteSpace(endpoint.ChartRef))
            missing.Add(nameof(KubernetesAgentEndpointDto.ChartRef));

        if (missing.Count > 0)
        {
            error = $"KubernetesAgent metadata missing for machine {machineId}: {string.Join(", ", missing)}";
            return false;
        }

        error = null;
        return true;
    }

    private static bool RequiresUpgrade(string currentVersion, string latestVersion)
    {
        Version.TryParse(latestVersion, out var latest);

        if (!Version.TryParse(currentVersion, out var current))
            return true;

        return current < latest;
    }

    private static string BuildUpgradeScript(KubernetesAgentEndpointDto endpoint, string latestVersion)
    {
        return string.Join(" \\\n", new[]
        {
            "helm upgrade --install --rollback-on-failure",
            $"--version \"{latestVersion}\"",
            "--reuse-values",
            $"--namespace {endpoint.HelmNamespace}",
            endpoint.ReleaseName,
            endpoint.ChartRef
        });
    }
}
