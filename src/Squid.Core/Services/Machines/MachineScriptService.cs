using System.Net;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Account;
using Squid.Core.Services.Authentication;
using Squid.Message.Commands.Machine;
using Squid.Message.Response;

namespace Squid.Core.Services.Machines;

public interface IMachineScriptService : IScopedDependency
{
    Task<GenerateKubernetesAgentInstallScriptResponse> GenerateKubernetesAgentInstallScriptAsync(GenerateKubernetesAgentInstallScriptCommand command, CancellationToken ct);

    Task<GenerateKubernetesAgentUpgradeScriptResponse> GenerateKubernetesAgentUpgradeScriptAsync(GenerateKubernetesAgentUpgradeScriptCommand command, CancellationToken ct);
}

public partial class MachineScriptService : IMachineScriptService
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserTokenService _userTokenService;
    private readonly IAccountService _accountService;
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IAgentVersionProvider _agentVersionProvider;

    public MachineScriptService(
        ICurrentUser currentUser,
        IUserTokenService userTokenService,
        IAccountService accountService,
        IMachineDataProvider machineDataProvider,
        IAgentVersionProvider agentVersionProvider)
    {
        _currentUser = currentUser;
        _userTokenService = userTokenService;
        _accountService = accountService;
        _machineDataProvider = machineDataProvider;
        _agentVersionProvider = agentVersionProvider;
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
