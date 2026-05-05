using System.Net;
using Squid.Core.Services.Account;
using Squid.Core.Services.Machines.Scripts.Tentacle;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Commands.Machine;
using Squid.Message.Response;

namespace Squid.Core.Services.Machines;

public interface IMachineScriptService : IScopedDependency
{
    Task<GenerateKubernetesAgentInstallScriptResponse> GenerateKubernetesAgentInstallScriptAsync(GenerateKubernetesAgentInstallScriptCommand command, CancellationToken ct);

    Task<GenerateKubernetesAgentUpgradeScriptResponse> GenerateKubernetesAgentUpgradeScriptAsync(GenerateKubernetesAgentUpgradeScriptCommand command, CancellationToken ct);

    Task<GenerateTentacleInstallScriptResponse> GenerateTentacleInstallScriptAsync(GenerateTentacleInstallScriptCommand command, CancellationToken ct);
}

public partial class MachineScriptService : IMachineScriptService
{
    private readonly IAccountService _accountService;
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IAgentVersionProvider _agentVersionProvider;
    private readonly SelfCertSetting _selfCertSetting;
    private readonly IEnumerable<ITentacleInstallScriptBuilder> _tentacleScriptBuilders;
    private readonly ITentacleCommsUrlProbe _commsUrlProbe;
    private readonly ITentacleVersionRegistry _tentacleVersionRegistry;

    public MachineScriptService(
        IAccountService accountService,
        IMachineDataProvider machineDataProvider,
        IAgentVersionProvider agentVersionProvider,
        SelfCertSetting selfCertSetting,
        IEnumerable<ITentacleInstallScriptBuilder> tentacleScriptBuilders,
        ITentacleCommsUrlProbe commsUrlProbe,
        ITentacleVersionRegistry tentacleVersionRegistry)
    {
        _accountService = accountService;
        _machineDataProvider = machineDataProvider;
        _agentVersionProvider = agentVersionProvider;
        _selfCertSetting = selfCertSetting;
        _tentacleScriptBuilders = tentacleScriptBuilders;
        _commsUrlProbe = commsUrlProbe;
        _tentacleVersionRegistry = tentacleVersionRegistry;
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
