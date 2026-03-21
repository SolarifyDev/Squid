using System.Net;
using Squid.Message.Commands.Machine;
using Squid.Message.Constants;

namespace Squid.Core.Services.Machines;

public partial class MachineScriptService
{
    private const string DefaultKubernetesAgentChartRef = "oci://registry-1.docker.io/squidcd/kubernetes-agent";

    public async Task<GenerateKubernetesAgentInstallScriptResponse> GenerateKubernetesAgentInstallScriptAsync(GenerateKubernetesAgentInstallScriptCommand command, CancellationToken ct)
    {
        if (command == null)
            return Fail<GenerateKubernetesAgentInstallScriptResponse, GenerateKubernetesAgentInstallScriptData>(HttpStatusCode.BadRequest, "Command cannot be null");

        try
        {
            var subscriptionId = Guid.NewGuid().ToString("N");
            var apiKeyResult = await TryCreateApiKeyAsync(subscriptionId, ct).ConfigureAwait(false);

            if (!apiKeyResult.Success)
            {
                return Fail<GenerateKubernetesAgentInstallScriptResponse, GenerateKubernetesAgentInstallScriptData>(apiKeyResult.Code, apiKeyResult.Message);
            }

            var storageType = command.StorageType ?? "builtin-nfs";

            var data = new GenerateKubernetesAgentInstallScriptData
            {
                SubscriptionId = subscriptionId,
                NfsCsiDriverScript = BuildNfsCsiDriverScript(storageType),
                AgentInstallScript = BuildAgentInstallScript(command, subscriptionId, apiKeyResult.ApiKey),
                NfsCsiDriverRequired = IsBuiltinNfs(storageType),
            };

            return Success<GenerateKubernetesAgentInstallScriptResponse, GenerateKubernetesAgentInstallScriptData>(data);
        }
        catch (Exception ex)
        {
            return Fail<GenerateKubernetesAgentInstallScriptResponse, GenerateKubernetesAgentInstallScriptData>(HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    private async Task<(bool Success, string ApiKey, HttpStatusCode Code, string Message)> TryCreateApiKeyAsync(string subscriptionId, CancellationToken ct)
    {
        var description = $"KubernetesAgent:{subscriptionId}";
        var result = await _accountService.CreateApiKeyAsync(CurrentUsers.InternalUser.Id, description, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result?.ApiKey))
            return (false, null, HttpStatusCode.InternalServerError, "Failed to create API key");

        return (true, result.ApiKey, HttpStatusCode.OK, null);
    }

    private static bool IsBuiltinNfs(string storageType)
        => string.IsNullOrWhiteSpace(storageType) || storageType == "builtin-nfs";

    private static string BuildNfsCsiDriverScript(string storageType)
    {
        if (!IsBuiltinNfs(storageType)) return null;

        return JoinLines(
            "helm upgrade --install --rollback-on-failure",
            "--repo https://raw.githubusercontent.com/kubernetes-csi/csi-driver-nfs/master/charts",
            "--namespace kube-system",
            "csi-driver-nfs",
            "csi-driver-nfs");
    }

    private static string BuildAgentInstallScript(
        GenerateKubernetesAgentInstallScriptCommand command, string subscriptionId, string apiKey)
    {
        var releaseName = $"squid-agent-{subscriptionId[..8]}";
        var agentName = string.IsNullOrWhiteSpace(command.AgentName) ? releaseName : command.AgentName;
        var chartRef = string.IsNullOrWhiteSpace(command.ChartRef) ? DefaultKubernetesAgentChartRef : command.ChartRef;

        var roles = FormatHelmArray(command.Tags);
        var environments = FormatHelmArray(command.Environments);

        var lines = new List<string>
        {
            "helm upgrade --install --rollback-on-failure",
            $"--set tentacle.serverUrl=\"{command.ServerUrl}\"",
            $"--set tentacle.serverCommsUrl=\"{command.ServerCommsUrl}\"",
            $"--set tentacle.apiKey=\"{apiKey}\"",
            $"--set tentacle.machineName=\"{agentName}\"",
            $"--set tentacle.roles=\"{roles}\"",
            $"--set tentacle.environments=\"{environments}\"",
            $"--set tentacle.spaceId=\"{command.SpaceId}\"",
            $"--set tentacle.flavor=\"KubernetesAgent\"",
            $"--set tentacle.subscriptionId=\"{subscriptionId}\"",
            $"--set tentacle.chartRef=\"{chartRef}\""
        };

        if (!string.IsNullOrWhiteSpace(command.DefaultNamespace))
            lines.Add($"--set kubernetes.namespace=\"{command.DefaultNamespace}\"");

        AppendStorageValues(lines, command);

        lines.Add("--version \"1.*.*\"");
        lines.Add("--create-namespace --namespace squid-agent");
        lines.Add(releaseName);
        lines.Add(chartRef);

        return JoinLines(lines.ToArray());
    }

    private static void AppendStorageValues(List<string> lines, GenerateKubernetesAgentInstallScriptCommand command)
    {
        var storageType = command.StorageType ?? "builtin-nfs";

        switch (storageType)
        {
            case "external-nfs":
                lines.Add($"--set workspace.nfs.server=\"{command.NfsServer}\"");
                if (!string.IsNullOrWhiteSpace(command.NfsPath))
                    lines.Add($"--set workspace.nfs.path=\"{command.NfsPath}\"");
                break;
            case "custom":
                if (!string.IsNullOrWhiteSpace(command.StorageClassName))
                    lines.Add($"--set workspace.storageClassName=\"{command.StorageClassName}\"");
                break;
        }
    }

    private static string FormatHelmArray<T>(IEnumerable<T> items)
    {
        if (items == null)
            return "{}";

        var values = items.ToList();
        return values.Count == 0 ? "{}" : $"{{{string.Join(",", values)}}}";
    }

    private static string JoinLines(params string[] lines)
    {
        return string.Join(" \\\n", lines);
    }
}
