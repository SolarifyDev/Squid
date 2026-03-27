using System.Text.Json;
using Squid.Message.Enums;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public class EndpointContext
{
    public string EndpointJson { get; set; }
    
    public Dictionary<EndpointResourceType, string> ResolvedResources { get; set; } = new();

    public ResolvedAuthenticationAccountData GetAccountData()
    {
        if (!ResolvedResources.TryGetValue(EndpointResourceType.AuthenticationAccount, out var json)) return null;

        return JsonSerializer.Deserialize<ResolvedAuthenticationAccountData>(json, SquidJsonDefaults.CaseInsensitive);
    }

    public void SetAccountData(AccountType accountType, string credentialsJson)
    {
        ResolvedResources[EndpointResourceType.AuthenticationAccount] = JsonSerializer.Serialize(new ResolvedAuthenticationAccountData { AuthenticationAccountType = accountType, CredentialsJson = credentialsJson });
    }

    public string GetCertificate(EndpointResourceType type)
    {
        ResolvedResources.TryGetValue(type, out var data);

        return data;
    }

    public void SetCertificate(EndpointResourceType type, string certificateData)
    {
        ResolvedResources[type] = certificateData;
    }
}

public class ResolvedAuthenticationAccountData
{
    public AccountType AuthenticationAccountType { get; set; }
    public string CredentialsJson { get; set; }
}

public class ScriptContext
{
    public EndpointContext Endpoint { get; set; }
    public ScriptSyntax Syntax { get; set; }
    public List<VariableDto> Variables { get; set; }
    public Dictionary<string, string> ActionProperties { get; set; }
}
