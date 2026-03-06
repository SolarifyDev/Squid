using System.Net;
using Squid.Core.Persistence.Entities.Account;
using Squid.Message.Commands.Machine;

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
            var tokenResult = await TryGenerateBearerTokenAsync(ct).ConfigureAwait(false);

            if (!tokenResult.Success)
            {
                return Fail<GenerateKubernetesAgentInstallScriptResponse, GenerateKubernetesAgentInstallScriptData>(tokenResult.Code, tokenResult.Message);
            }

            var data = new GenerateKubernetesAgentInstallScriptData
            {
                SubscriptionId = subscriptionId,
                NfsCsiDriverScript = BuildNfsCsiDriverScript(),
                AgentInstallScript = BuildAgentInstallScript(command, subscriptionId, tokenResult.Token),
            };

            return Success<GenerateKubernetesAgentInstallScriptResponse, GenerateKubernetesAgentInstallScriptData>(data);
        }
        catch (Exception ex)
        {
            return Fail<GenerateKubernetesAgentInstallScriptResponse, GenerateKubernetesAgentInstallScriptData>(HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    private async Task<(bool Success, string Token, HttpStatusCode Code, string Message)> TryGenerateBearerTokenAsync(CancellationToken ct)
    {
        var userId = _currentUser.Id;

        if (userId == null)
            return (false, null, HttpStatusCode.Unauthorized, "Cannot resolve current user");

        var userAccount = await _accountService.GetByIdAsync(userId.Value, ct).ConfigureAwait(false);

        if (userAccount == null)
            return (false, null, HttpStatusCode.Unauthorized, $"User account {userId} not found");

        var account = new UserAccount
        {
            Id = userAccount.Id,
            UserName = userAccount.UserName,
            DisplayName = userAccount.DisplayName,
            IsSystem = false
        };

        var (token, _) = _userTokenService.GenerateToken(account);

        if (string.IsNullOrWhiteSpace(token))
            return (false, null, HttpStatusCode.InternalServerError, "Failed to generate bearer token");

        return (true, token, HttpStatusCode.OK, null);
    }

    private static string BuildNfsCsiDriverScript()
    {
        return JoinLines(
            "helm upgrade --install --rollback-on-failure",
            "--repo https://raw.githubusercontent.com/kubernetes-csi/csi-driver-nfs/master/charts",
            "--namespace kube-system",
            "csi-driver-nfs",
            "csi-driver-nfs");
    }

    private static string BuildAgentInstallScript(
        GenerateKubernetesAgentInstallScriptCommand command, string subscriptionId, string bearerToken)
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
            $"--set tentacle.bearerToken=\"{bearerToken}\"",
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

        lines.Add("--version \"1.*.*\"");
        lines.Add("--create-namespace --namespace squid-agent");
        lines.Add(releaseName);
        lines.Add(chartRef);

        return JoinLines(lines.ToArray());
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
