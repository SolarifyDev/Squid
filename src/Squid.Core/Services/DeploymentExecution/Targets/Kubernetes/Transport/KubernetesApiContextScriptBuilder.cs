using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

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
        var endpoint = EndpointVariableFactory.TryDeserialize<Message.Models.Deployments.Machine.KubernetesApiEndpointDto>(context?.Endpoint?.EndpointJson);

        var clusterCert = context?.Endpoint?.GetCertificate(EndpointResourceType.ClusterCertificate) ?? string.Empty;

        var accountData = context?.Endpoint?.GetAccountData();

        var creds = accountData != null
            ? DeploymentAccountCredentialsConverter.Deserialize(accountData.AuthenticationAccountType, accountData.CredentialsJson)
            : null;

        var tokenCreds = creds as TokenCredentials;
        var upCreds = creds as UsernamePasswordCredentials;
        var certCreds = creds as ClientCertificateCredentials;
        var awsCreds = creds as AwsCredentials;

        var syntax = context?.Syntax ?? Message.Models.Deployments.Execution.ScriptSyntax.Bash;
        var isBash = syntax == Message.Models.Deployments.Execution.ScriptSyntax.Bash;
        var templateName = isBash ? "KubectlContext.sh" : "KubectlContext.ps1";
        var template = UtilService.GetEmbeddedScriptContent(templateName);

        string Esc(string value) => isBash
            ? ShellEscapeHelper.EscapeBash(value ?? string.Empty)
            : ShellEscapeHelper.EscapePowerShell(value ?? string.Empty);

        template = template
            .Replace("{{KubectlExe}}", customKubectlPath ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{ClusterUrl}}", endpoint?.ClusterUrl ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{AccountType}}", accountData?.AuthenticationAccountType.ToString() ?? "Token", StringComparison.Ordinal)
            .Replace("{{SkipTlsVerification}}", endpoint?.SkipTlsVerification ?? KubernetesBooleanValues.False, StringComparison.Ordinal)
            .Replace("{{Namespace}}", ResolveNamespace(context, endpoint), StringComparison.Ordinal)
            .Replace("{{ClusterCertificate}}", Esc(clusterCert), StringComparison.Ordinal)
            .Replace("{{Token}}", Esc(tokenCreds?.Token), StringComparison.Ordinal)
            .Replace("{{Username}}", Esc(upCreds?.Username), StringComparison.Ordinal)
            .Replace("{{Password}}", Esc(upCreds?.Password), StringComparison.Ordinal)
            .Replace("{{ClientCertificateData}}", Esc(certCreds?.ClientCertificateData), StringComparison.Ordinal)
            .Replace("{{ClientCertificateKeyData}}", Esc(certCreds?.ClientCertificateKeyData), StringComparison.Ordinal)
            .Replace("{{AccessKey}}", Esc(awsCreds?.AccessKey), StringComparison.Ordinal)
            .Replace("{{SecretKey}}", Esc(awsCreds?.SecretKey), StringComparison.Ordinal)
            .Replace("{{AwsClusterName}}", endpoint?.AwsClusterName ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{AwsRegion}}", endpoint?.AwsRegion ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{UserScript}}", userScript ?? string.Empty, StringComparison.Ordinal);

        return template;
    }

    private static string ResolveNamespace(ScriptContext context, Message.Models.Deployments.Machine.KubernetesApiEndpointDto endpoint)
    {
        if (context?.ActionProperties != null && context.ActionProperties.Count > 0)
            return KubernetesPropertyParser.GetNamespace(context.ActionProperties);

        return endpoint?.Namespace ?? KubernetesDefaultValues.Namespace;
    }
}
