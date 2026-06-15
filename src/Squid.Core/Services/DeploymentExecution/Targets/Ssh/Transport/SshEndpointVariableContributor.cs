using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Security;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public class SshEndpointVariableContributor : IEndpointVariableContributor
{
    private readonly IAtRestSecretProtector _protector;

    // Optional: when DI-resolved on the deploy path the real protector decrypts the at-rest ProxyPassword
    // before it is contributed as the SSH connection variable. When constructed without one (the SSH
    // health check's internal use — which reads ProxyPassword separately — or tests), the value passes
    // through; since Unprotect is read-both, a legacy plaintext is identical either way.
    public SshEndpointVariableContributor(IAtRestSecretProtector protector = null)
    {
        _protector = protector;
    }

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

        // Decrypt the at-rest proxy password before it becomes the SSH connection variable. Read-both:
        // a legacy unprefixed plaintext passes through unchanged; no protector (non-deploy use) = passthrough.
        var proxyPassword = _protector != null
            ? _protector.Unprotect(endpoint.ProxyPassword, SshEndpointDto.ProxyPasswordKdfScope)
            : endpoint.ProxyPassword;

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
            EndpointVariableFactory.Make(SpecialVariables.Ssh.ProxyPassword, proxyPassword ?? string.Empty, isSensitive: true),
            EndpointVariableFactory.Make(SpecialVariables.Ssh.PackageBaseDirectory, PackageBaseDirectory(endpoint.RemoteWorkingDirectory))
        };

        if (accountData != null)
        {
            vars.Add(EndpointVariableFactory.Make(SpecialVariables.Account.AccountType, accountData.AuthenticationAccountType.ToString()));
            vars.Add(EndpointVariableFactory.Make(SpecialVariables.Account.CredentialsJson, accountData.CredentialsJson ?? string.Empty, isSensitive: true));
            vars.AddRange(AccountVariableExpander.Expand(accountData));
        }

        return vars;
    }

    private static string PackageBaseDirectory(string remoteWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(remoteWorkingDirectory))
            return "~/.squid/Packages";

        return $"{remoteWorkingDirectory.TrimEnd('/')}/Packages";
    }
}
