using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public class SshEndpointVariableContributor : IEndpointVariableContributor
{
    public EndpointResourceReferences ParseResourceReferences(string endpointJson)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<SshEndpointDto>(endpointJson);

        if (endpoint == null) return new EndpointResourceReferences();

        return new EndpointResourceReferences
        {
            References = endpoint.ResourceReferences ?? new()
        };
    }

    public List<VariableDto> ContributeVariables(EndpointContext context)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<SshEndpointDto>(context.EndpointJson);

        if (endpoint == null) return new List<VariableDto>();

        var accountData = context.GetAccountData();

        var vars = new List<VariableDto>
        {
            EndpointVariableFactory.Make(SpecialVariables.Machine.Hostname, endpoint.Host ?? string.Empty),
            EndpointVariableFactory.Make(SpecialVariables.Ssh.Host, endpoint.Host ?? string.Empty),
            EndpointVariableFactory.Make(SpecialVariables.Ssh.Port, endpoint.Port.ToString()),
            EndpointVariableFactory.Make(SpecialVariables.Ssh.Fingerprint, endpoint.Fingerprint ?? string.Empty),
            EndpointVariableFactory.Make(SpecialVariables.Ssh.RemoteWorkingDirectory, endpoint.RemoteWorkingDirectory ?? string.Empty),
            EndpointVariableFactory.Make(SpecialVariables.Ssh.ProxyType, endpoint.ProxyType.ToString()),
            EndpointVariableFactory.Make(SpecialVariables.Ssh.ProxyHost, endpoint.ProxyHost ?? string.Empty),
            EndpointVariableFactory.Make(SpecialVariables.Ssh.ProxyPort, endpoint.ProxyPort.ToString()),
            EndpointVariableFactory.Make(SpecialVariables.Ssh.ProxyUsername, endpoint.ProxyUsername ?? string.Empty),
            EndpointVariableFactory.Make(SpecialVariables.Ssh.ProxyPassword, endpoint.ProxyPassword ?? string.Empty, isSensitive: true)
        };

        if (accountData != null)
        {
            vars.Add(EndpointVariableFactory.Make(SpecialVariables.Account.AccountType, accountData.AuthenticationAccountType.ToString()));
            vars.Add(EndpointVariableFactory.Make(SpecialVariables.Account.CredentialsJson, accountData.CredentialsJson ?? string.Empty, isSensitive: true));
            vars.AddRange(AccountVariableExpander.Expand(accountData));
        }

        return vars;
    }
}
