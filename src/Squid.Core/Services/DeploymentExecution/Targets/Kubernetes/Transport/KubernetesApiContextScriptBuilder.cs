using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Account;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public interface IKubernetesApiContextScriptBuilder : IScopedDependency
{
    string WrapWithContext(
        string userScript,
        KubernetesApiEndpointDto endpoint,
        DeploymentAccount account,
        ScriptSyntax syntax,
        string customKubectlPath = null);
}

public class KubernetesApiContextScriptBuilder : IKubernetesApiContextScriptBuilder
{
    public string WrapWithContext(
        string userScript,
        KubernetesApiEndpointDto endpoint,
        DeploymentAccount account,
        ScriptSyntax syntax,
        string customKubectlPath = null)
    {
        var creds = account != null
            ? DeploymentAccountCredentialsConverter.Deserialize(account.AccountType, account.Credentials)
            : null;

        var tokenCreds = creds as TokenCredentials;
        var upCreds = creds as UsernamePasswordCredentials;
        var certCreds = creds as ClientCertificateCredentials;
        var awsCreds = creds as AwsCredentials;

        var templateName = syntax == ScriptSyntax.Bash ? "KubectlContext.sh" : "KubectlContext.ps1";
        var template = UtilService.GetEmbeddedScriptContent(templateName);

        template = template
            .Replace("{{KubectlExe}}", customKubectlPath ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{ClusterUrl}}", endpoint?.ClusterUrl ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{AccountType}}", account?.AccountType.ToString() ?? "Token", StringComparison.Ordinal)
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
