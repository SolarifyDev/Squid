using System.Text;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Account;
using Squid.Core.Services.Authentication;
using Squid.Core.Services.Identity;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines;

public interface IMachineInstallScriptService : IScopedDependency
{
    Task<GenerateKubernetesAgentInstallScriptData> GenerateKubernetesAgentScriptAsync(
        GenerateKubernetesAgentInstallScriptCommand command, CancellationToken ct);
}

public class MachineInstallScriptService : IMachineInstallScriptService
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserTokenService _userTokenService;
    private readonly IAccountService _accountService;

    public MachineInstallScriptService(
        ICurrentUser currentUser,
        IUserTokenService userTokenService,
        IAccountService accountService)
    {
        _currentUser = currentUser;
        _userTokenService = userTokenService;
        _accountService = accountService;
    }

    public async Task<GenerateKubernetesAgentInstallScriptData> GenerateKubernetesAgentScriptAsync(
        GenerateKubernetesAgentInstallScriptCommand command, CancellationToken ct)
    {
        var subscriptionId = Guid.NewGuid().ToString("N");
        var bearerToken = await GenerateBearerTokenAsync(ct).ConfigureAwait(false);

        var nfsCsiDriverScript = BuildNfsCsiDriverScript();
        var agentInstallScript = BuildAgentInstallScript(
            command, subscriptionId, bearerToken);

        return new GenerateKubernetesAgentInstallScriptData
        {
            NfsCsiDriverScript = nfsCsiDriverScript,
            AgentInstallScript = agentInstallScript,
            SubscriptionId = subscriptionId
        };
    }

    private async Task<string> GenerateBearerTokenAsync(CancellationToken ct)
    {
        var userId = _currentUser.Id;

        if (userId == null)
            throw new InvalidOperationException("Cannot resolve current user");

        var userAccount = await _accountService.GetByIdAsync(userId.Value, ct).ConfigureAwait(false);

        if (userAccount == null)
            throw new InvalidOperationException($"User account {userId} not found");

        var account = new UserAccount
        {
            Id = userAccount.Id,
            UserName = userAccount.UserName,
            DisplayName = userAccount.DisplayName,
            IsSystem = false
        };

        var (token, _) = _userTokenService.GenerateToken(account);

        return token;
    }

    private static string BuildNfsCsiDriverScript()
    {
        return "helm repo add csi-driver-nfs https://raw.githubusercontent.com/kubernetes-csi/csi-driver-nfs/master/charts && " +
               "helm install csi-driver-nfs csi-driver-nfs/csi-driver-nfs --namespace kube-system --set kubeletDir=/var/lib/kubelet";
    }

    private static string BuildAgentInstallScript(
        GenerateKubernetesAgentInstallScriptCommand command,
        string subscriptionId,
        string bearerToken)
    {
        var agentName = string.IsNullOrWhiteSpace(command.AgentName)
            ? $"squid-agent-{subscriptionId[..8]}"
            : command.AgentName;

        var environmentIds = string.Join(",", command.EnvironmentIds);
        var roles = string.Join(",", command.Tags);

        var sb = new StringBuilder();
        sb.Append("helm upgrade --install ");
        sb.Append(agentName);
        sb.Append(" oci://registry-1.docker.io/squid/squid-tentacle");
        sb.Append($" --set tentacle.serverUrl=\"{command.ServerUrl}\"");
        sb.Append($" --set tentacle.serverCommsUrl=\"{command.ServerCommsUrl}\"");
        sb.Append($" --set tentacle.bearerToken=\"{bearerToken}\"");
        sb.Append($" --set tentacle.machineName=\"{agentName}\"");
        sb.Append($" --set tentacle.roles=\"{roles}\"");
        sb.Append($" --set tentacle.environmentIds=\"{environmentIds}\"");
        sb.Append($" --set tentacle.spaceId=\"{command.SpaceId}\"");
        sb.Append($" --set tentacle.flavor=\"KubernetesAgent\"");
        sb.Append($" --set tentacle.subscriptionId=\"{subscriptionId}\"");
        sb.Append($" --set kubernetes.namespace=\"default\"");

        return sb.ToString();
    }
}
