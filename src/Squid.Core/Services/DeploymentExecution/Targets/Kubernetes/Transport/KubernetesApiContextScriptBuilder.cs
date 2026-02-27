using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Account;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public interface IKubernetesApiContextScriptBuilder : IScopedDependency
{
    string WrapWithContext(
        string userScript,
        ScriptContext context,
        string customKubectlPath = null);
}

public class KubernetesApiContextScriptBuilder : IKubernetesApiContextScriptBuilder
{
    public string WrapWithContext(
        string userScript,
        ScriptContext context,
        string customKubectlPath = null)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<KubernetesApiEndpointDto>(context?.Endpoint?.EndpointJson);

        var accountType = context?.Endpoint?.AccountType;
        var credentialsJson = context?.Endpoint?.CredentialsJson;

        var creds = accountType.HasValue && credentialsJson != null
            ? DeploymentAccountCredentialsConverter.Deserialize(accountType.Value, credentialsJson)
            : null;

        var tokenCreds = creds as TokenCredentials;
        var upCreds = creds as UsernamePasswordCredentials;
        var certCreds = creds as ClientCertificateCredentials;
        var awsCreds = creds as AwsCredentials;

        var syntax = context?.Syntax ?? Message.Models.Deployments.Execution.ScriptSyntax.Bash;
        var templateName = syntax == Message.Models.Deployments.Execution.ScriptSyntax.Bash ? "KubectlContext.sh" : "KubectlContext.ps1";
        var template = UtilService.GetEmbeddedScriptContent(templateName);

        template = template
            .Replace("{{KubectlExe}}", customKubectlPath ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{ClusterUrl}}", endpoint?.ClusterUrl ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{AccountType}}", accountType?.ToString() ?? "Token", StringComparison.Ordinal)
            .Replace("{{SkipTlsVerification}}", endpoint?.SkipTlsVerification ?? "False", StringComparison.Ordinal)
            .Replace("{{Namespace}}", endpoint?.Namespace ?? "default", StringComparison.Ordinal)
            .Replace("{{ClusterCertificate}}", endpoint?.ClusterCertificate ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{Token}}", tokenCreds?.Token ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{Username}}", upCreds?.Username ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{Password}}", upCreds?.Password ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{ClientCertificateData}}", certCreds?.ClientCertificateData ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{ClientCertificateKeyData}}", certCreds?.ClientCertificateKeyData ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{AccessKey}}", awsCreds?.AccessKey ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{SecretKey}}", awsCreds?.SecretKey ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{AwsClusterName}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{AwsRegion}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{UserScript}}", userScript ?? string.Empty, StringComparison.Ordinal);

        return template;
    }
}
