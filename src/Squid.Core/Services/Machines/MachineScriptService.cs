using System.Net;
using Squid.Core.Services.Account;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Commands.Machine;
using Squid.Message.Response;

namespace Squid.Core.Services.Machines;

public interface IMachineScriptService : IScopedDependency
{
    Task<GenerateKubernetesAgentInstallScriptResponse> GenerateKubernetesAgentInstallScriptAsync(GenerateKubernetesAgentInstallScriptCommand command, CancellationToken ct);

    Task<GenerateKubernetesAgentUpgradeScriptResponse> GenerateKubernetesAgentUpgradeScriptAsync(GenerateKubernetesAgentUpgradeScriptCommand command, CancellationToken ct);

    Task<GenerateLinuxTentacleInstallScriptResponse> GenerateLinuxTentacleInstallScriptAsync(GenerateLinuxTentacleInstallScriptCommand command, CancellationToken ct);
}

public partial class MachineScriptService : IMachineScriptService
{
    private readonly IAccountService _accountService;
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IAgentVersionProvider _agentVersionProvider;
    private readonly SelfCertSetting _selfCertSetting;

    public MachineScriptService(
        IAccountService accountService,
        IMachineDataProvider machineDataProvider,
        IAgentVersionProvider agentVersionProvider,
        SelfCertSetting selfCertSetting)
    {
        _accountService = accountService;
        _machineDataProvider = machineDataProvider;
        _agentVersionProvider = agentVersionProvider;
        _selfCertSetting = selfCertSetting;
    }

    private static TResponse Success<TResponse, TData>(TData data) where TResponse : SquidResponse<TData>, new()
    {
        return new TResponse
        {
            Code = HttpStatusCode.OK,
            Msg = "Success",
            Data = data
        };
    }

    private static TResponse Fail<TResponse, TData>(HttpStatusCode code, string message) where TResponse : SquidResponse<TData>, new()
    {
        return new TResponse
        {
            Code = code,
            Msg = message
        };
    }
}
