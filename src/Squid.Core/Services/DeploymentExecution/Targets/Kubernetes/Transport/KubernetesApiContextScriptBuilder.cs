using Squid.Core.Services.Common;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Services.DeploymentExecution.Transport;

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

        var providerConfig = endpoint != null
            ? KubernetesApiEndpointProviderConfigConverter.Deserialize(endpoint.ProviderType, endpoint.ProviderConfig)
            : null;

        var awsEks = providerConfig as KubernetesApiAwsEksConfig;
        var azureAks = providerConfig as KubernetesApiAzureAksConfig;
        var gcpGke = providerConfig as KubernetesApiGcpGkeConfig;
        var proxy = endpoint?.Proxy;

        var clusterCert = context?.Endpoint?.GetCertificate(EndpointResourceType.ClusterCertificate) ?? string.Empty;

        var accountData = context?.Endpoint?.GetAccountData();

        var creds = accountData != null
            ? DeploymentAccountCredentialsConverter.Deserialize(accountData.AuthenticationAccountType, accountData.CredentialsJson)
            : null;

        var tokenCreds = creds as TokenCredentials;
        var upCreds = creds as UsernamePasswordCredentials;
        var certCreds = creds as ClientCertificateCredentials;
        var awsCreds = creds as AwsCredentials;
        var azureCreds = creds as AzureServicePrincipalCredentials;
        var azureOidcCreds = creds as AzureOidcCredentials;
        var gcpCreds = creds as GcpCredentials;
        var awsOidcCreds = creds as AwsOidcCredentials;

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
            // Token
            .Replace("{{Token}}", Esc(tokenCreds?.Token), StringComparison.Ordinal)
            // UsernamePassword
            .Replace("{{Username}}", Esc(upCreds?.Username), StringComparison.Ordinal)
            .Replace("{{Password}}", Esc(upCreds?.Password), StringComparison.Ordinal)
            // ClientCertificate
            .Replace("{{ClientCertificateData}}", Esc(certCreds?.ClientCertificateData), StringComparison.Ordinal)
            .Replace("{{ClientCertificateKeyData}}", Esc(certCreds?.ClientCertificateKeyData), StringComparison.Ordinal)
            // AWS static
            .Replace("{{AccessKey}}", Esc(awsCreds?.AccessKey), StringComparison.Ordinal)
            .Replace("{{SecretKey}}", Esc(awsCreds?.SecretKey), StringComparison.Ordinal)
            .Replace("{{AwsClusterName}}", awsEks?.ClusterName ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{AwsRegion}}", awsEks?.Region ?? string.Empty, StringComparison.Ordinal)
            // AWS OIDC
            .Replace("{{AwsRoleArn}}", Esc(awsOidcCreds?.RoleArn), StringComparison.Ordinal)
            .Replace("{{AwsWebIdentityToken}}", Esc(awsOidcCreds?.WebIdentityToken), StringComparison.Ordinal)
            // Azure
            .Replace("{{AzureClientId}}", Esc(azureCreds?.ClientId ?? azureOidcCreds?.ClientId), StringComparison.Ordinal)
            .Replace("{{AzureTenantId}}", Esc(azureCreds?.TenantId ?? azureOidcCreds?.TenantId), StringComparison.Ordinal)
            .Replace("{{AzureKey}}", Esc(azureCreds?.Key), StringComparison.Ordinal)
            .Replace("{{AzureSubscriptionId}}", Esc(azureCreds?.SubscriptionNumber ?? azureOidcCreds?.SubscriptionNumber), StringComparison.Ordinal)
            .Replace("{{AzureOidcToken}}", Esc(azureOidcCreds?.Audience), StringComparison.Ordinal)
            .Replace("{{AksClusterName}}", azureAks?.ClusterName ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{AksClusterResourceGroup}}", azureAks?.ResourceGroup ?? string.Empty, StringComparison.Ordinal)
            // GCP
            .Replace("{{GcpJsonKey}}", Esc(gcpCreds?.JsonKey), StringComparison.Ordinal)
            .Replace("{{GkeClusterName}}", gcpGke?.ClusterName ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{GkeProject}}", gcpGke?.Project ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{GkeZone}}", gcpGke?.Zone ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{GkeRegion}}", gcpGke?.Region ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{GkeUseClusterInternalIp}}", gcpGke?.UseClusterInternalIp ?? KubernetesBooleanValues.False, StringComparison.Ordinal)
            // Proxy
            .Replace("{{ProxyHost}}", proxy?.Host ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{ProxyPort}}", proxy?.Port ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{ProxyUsername}}", Esc(proxy?.Username), StringComparison.Ordinal)
            .Replace("{{ProxyPassword}}", Esc(proxy?.Password), StringComparison.Ordinal)
            // User script
            .Replace("{{UserScript}}", userScript ?? string.Empty, StringComparison.Ordinal);

        return template;
    }

    private static string ResolveNamespace(ScriptContext context, KubernetesApiEndpointDto endpoint)
    {
        if (context?.ActionProperties != null && context.ActionProperties.Count > 0)
            return KubernetesPropertyParser.GetNamespace(context.ActionProperties);

        return endpoint?.Namespace ?? KubernetesDefaultValues.Namespace;
    }
}
