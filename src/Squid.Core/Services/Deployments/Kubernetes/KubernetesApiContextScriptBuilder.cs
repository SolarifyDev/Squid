using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Deployments.Kubernetes;

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
        var templateName = syntax == ScriptSyntax.Bash ? "KubectlContext.sh" : "KubectlContext.ps1";
        var template = UtilService.GetEmbeddedScriptContent(templateName);

        template = template
            .Replace("{{KubectlExe}}", customKubectlPath ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{ClusterUrl}}", endpoint?.ClusterUrl ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{AccountType}}", account?.AccountType.ToString() ?? "Token", StringComparison.Ordinal)
            .Replace("{{SkipTlsVerification}}", endpoint?.SkipTlsVerification ?? "False", StringComparison.Ordinal)
            .Replace("{{Namespace}}", endpoint?.Namespace ?? "default", StringComparison.Ordinal)
            .Replace("{{ClusterCertificate}}", endpoint?.ClusterCertificate ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{Token}}", account?.Token ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{Username}}", account?.Username ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{Password}}", account?.Password ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{ClientCertificateData}}", account?.ClientCertificateData ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{ClientCertificateKeyData}}", account?.ClientCertificateKeyData ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{AccessKey}}", account?.AccessKey ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{SecretKey}}", account?.SecretKey ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{AwsClusterName}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{AwsRegion}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{UserScript}}", userScript ?? string.Empty, StringComparison.Ordinal);

        return template;
    }
}
