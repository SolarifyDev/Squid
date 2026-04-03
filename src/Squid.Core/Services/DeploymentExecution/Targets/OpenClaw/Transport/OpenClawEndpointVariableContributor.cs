using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawEndpointVariableContributor : IEndpointVariableContributor
{
    public EndpointResourceReferences ParseResourceReferences(string endpointJson)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<OpenClawEndpointDto>(endpointJson);

        if (endpoint == null) return new EndpointResourceReferences();

        return new EndpointResourceReferences
        {
            References = endpoint.ResourceReferences ?? new()
        };
    }

    public List<VariableDto> ContributeVariables(EndpointContext context)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<OpenClawEndpointDto>(context.EndpointJson);

        if (endpoint == null) return new List<VariableDto>();

        var accountData = context.GetAccountData();
        var (gatewayToken, hooksToken) = ResolveTokens(endpoint, accountData);

        var wsUrl = ResolveWebSocketUrl(endpoint);

        var vars = new List<VariableDto>
        {
            EndpointVariableFactory.Make(SpecialVariables.OpenClaw.BaseUrl, endpoint.BaseUrl ?? string.Empty),
            EndpointVariableFactory.Make(SpecialVariables.OpenClaw.GatewayToken, gatewayToken, isSensitive: true),
            EndpointVariableFactory.Make(SpecialVariables.OpenClaw.HooksToken, hooksToken, isSensitive: true),
            EndpointVariableFactory.Make(SpecialVariables.OpenClaw.WebSocketUrl, wsUrl)
        };

        if (accountData != null)
        {
            vars.Add(EndpointVariableFactory.Make(SpecialVariables.Account.AccountType, accountData.AuthenticationAccountType.ToString()));
            vars.Add(EndpointVariableFactory.Make(SpecialVariables.Account.CredentialsJson, accountData.CredentialsJson ?? string.Empty, isSensitive: true));
            vars.AddRange(AccountVariableExpander.Expand(accountData));
        }

        return vars;
    }

    private static string ResolveWebSocketUrl(OpenClawEndpointDto endpoint)
    {
        if (!string.IsNullOrEmpty(endpoint.WebSocketUrl))
            return endpoint.WebSocketUrl;

        if (string.IsNullOrEmpty(endpoint.BaseUrl))
            return string.Empty;

        return DeriveWsUrl(endpoint.BaseUrl);
    }

    internal static string DeriveWsUrl(string httpUrl)
    {
        if (httpUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "wss://" + httpUrl[8..].TrimEnd('/');

        if (httpUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "ws://" + httpUrl[7..].TrimEnd('/');

        return string.Empty;
    }

    private static (string GatewayToken, string HooksToken) ResolveTokens(OpenClawEndpointDto endpoint, ResolvedAuthenticationAccountData accountData)
    {
        if (accountData is { AuthenticationAccountType: AccountType.OpenClawGateway })
        {
            var creds = DeploymentAccountCredentialsConverter
                .Deserialize(accountData.AuthenticationAccountType, accountData.CredentialsJson) as OpenClawGatewayCredentials;

            if (creds != null)
                return (creds.GatewayToken ?? string.Empty, creds.HooksToken ?? string.Empty);
        }

        return (endpoint.InlineGatewayToken ?? string.Empty, endpoint.InlineHooksToken ?? string.Empty);
    }
}
