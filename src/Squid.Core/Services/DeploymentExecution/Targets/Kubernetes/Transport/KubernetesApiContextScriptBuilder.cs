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
    string WrapWithContext(string userScript, ScriptContext context, string customKubectlPath = null);

    string BuildSetupScript(ScriptContext context, string customKubectlPath = null);
}

public class KubernetesApiContextScriptBuilder : IKubernetesApiContextScriptBuilder
{
    public string WrapWithContext(string userScript, ScriptContext context, string customKubectlPath = null)
    {
        var syntax = context?.Syntax ?? Message.Models.Deployments.Execution.ScriptSyntax.Bash;
        var isBash = syntax == Message.Models.Deployments.Execution.ScriptSyntax.Bash;
        var templateName = isBash ? "KubectlContext.sh" : "KubectlContext.ps1";
        var template = UtilService.GetEmbeddedScriptContent(templateName);

        var populated = PopulateTemplate(template, context, customKubectlPath);

        return populated.Replace("{{UserScript}}", userScript ?? string.Empty, StringComparison.Ordinal);
    }

    public string BuildSetupScript(ScriptContext context, string customKubectlPath = null)
    {
        var template = UtilService.GetEmbeddedScriptContent("KubectlContextSetup.sh");

        return PopulateTemplate(template, context, customKubectlPath);
    }

    private static string PopulateTemplate(string template, ScriptContext context, string customKubectlPath)
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
        var awsRoleCreds = creds as AwsRoleCredentials;
        var azureCreds = creds as AzureServicePrincipalCredentials;
        var azureOidcCreds = creds as AzureOidcCredentials;
        var gcpCreds = creds as GcpCredentials;
        var awsOidcCreds = creds as AwsOidcCredentials;

        string B64(string value) => ShellEscapeHelper.Base64Encode(value ?? string.Empty);

        var effectiveAccountType = awsEks?.UseInstanceRole == true
            ? "AwsEc2InstanceRole"
            : accountData?.AuthenticationAccountType.ToString() ?? "Token";

        return template
            .Replace("{{KubectlExe}}", B64(customKubectlPath), StringComparison.Ordinal)
            .Replace("{{ClusterUrl}}", B64(endpoint?.ClusterUrl), StringComparison.Ordinal)
            .Replace("{{AccountType}}", B64(effectiveAccountType), StringComparison.Ordinal)
            .Replace("{{SkipTlsVerification}}", B64(endpoint?.SkipTlsVerification ?? KubernetesBooleanValues.False), StringComparison.Ordinal)
            .Replace("{{Namespace}}", B64(ResolveNamespace(context, endpoint)), StringComparison.Ordinal)
            .Replace("{{ClusterCertificate}}", B64(clusterCert), StringComparison.Ordinal)
            // Token
            .Replace("{{Token}}", B64(tokenCreds?.Token), StringComparison.Ordinal)
            // UsernamePassword
            .Replace("{{Username}}", B64(upCreds?.Username), StringComparison.Ordinal)
            .Replace("{{Password}}", B64(upCreds?.Password), StringComparison.Ordinal)
            // ClientCertificate
            .Replace("{{ClientCertificateData}}", B64(certCreds?.ClientCertificateData), StringComparison.Ordinal)
            .Replace("{{ClientCertificateKeyData}}", B64(certCreds?.ClientCertificateKeyData), StringComparison.Ordinal)
            // AWS static + role
            .Replace("{{AccessKey}}", B64(awsCreds?.AccessKey ?? awsRoleCreds?.AccessKey), StringComparison.Ordinal)
            .Replace("{{SecretKey}}", B64(awsCreds?.SecretKey ?? awsRoleCreds?.SecretKey), StringComparison.Ordinal)
            .Replace("{{AwsClusterName}}", B64(awsEks?.ClusterName), StringComparison.Ordinal)
            .Replace("{{AwsRegion}}", B64(awsEks?.Region), StringComparison.Ordinal)
            .Replace("{{AwsAssumeRoleArn}}", B64(awsRoleCreds?.RoleArn), StringComparison.Ordinal)
            .Replace("{{AwsAssumeRoleSessionDuration}}", B64(awsRoleCreds?.SessionDuration), StringComparison.Ordinal)
            .Replace("{{AwsAssumeRoleExternalId}}", B64(awsRoleCreds?.ExternalId), StringComparison.Ordinal)
            // AWS OIDC
            .Replace("{{AwsRoleArn}}", B64(awsOidcCreds?.RoleArn), StringComparison.Ordinal)
            .Replace("{{AwsWebIdentityToken}}", B64(awsOidcCreds?.WebIdentityToken), StringComparison.Ordinal)
            // AWS endpoint-level
            .Replace("{{AwsUseInstanceRole}}", B64(awsEks?.UseInstanceRole == true ? "True" : ""), StringComparison.Ordinal)
            .Replace("{{AwsEndpointAssumeRoleArn}}", B64(awsEks?.AssumeRoleArn), StringComparison.Ordinal)
            .Replace("{{AwsEndpointAssumeRoleSessionDuration}}", B64(awsEks?.AssumeRoleSessionDuration), StringComparison.Ordinal)
            .Replace("{{AwsEndpointAssumeRoleExternalId}}", B64(awsEks?.AssumeRoleExternalId), StringComparison.Ordinal)
            // Azure
            .Replace("{{AzureClientId}}", B64(azureCreds?.ClientId ?? azureOidcCreds?.ClientId), StringComparison.Ordinal)
            .Replace("{{AzureTenantId}}", B64(azureCreds?.TenantId ?? azureOidcCreds?.TenantId), StringComparison.Ordinal)
            .Replace("{{AzureKey}}", B64(azureCreds?.Key), StringComparison.Ordinal)
            .Replace("{{AzureSubscriptionId}}", B64(azureCreds?.SubscriptionNumber ?? azureOidcCreds?.SubscriptionNumber), StringComparison.Ordinal)
            .Replace("{{AzureOidcToken}}", B64(azureOidcCreds?.Jwt), StringComparison.Ordinal)
            .Replace("{{AksClusterName}}", B64(azureAks?.ClusterName), StringComparison.Ordinal)
            .Replace("{{AksClusterResourceGroup}}", B64(azureAks?.ResourceGroup), StringComparison.Ordinal)
            .Replace("{{AksUseAdminCredentials}}", B64(azureAks?.UseAdminCredentials == true ? "True" : ""), StringComparison.Ordinal)
            // GCP
            .Replace("{{GcpJsonKey}}", B64(gcpCreds?.JsonKey), StringComparison.Ordinal)
            .Replace("{{GkeClusterName}}", B64(gcpGke?.ClusterName), StringComparison.Ordinal)
            .Replace("{{GkeProject}}", B64(gcpGke?.Project), StringComparison.Ordinal)
            .Replace("{{GkeZone}}", B64(gcpGke?.Zone), StringComparison.Ordinal)
            .Replace("{{GkeRegion}}", B64(gcpGke?.Region), StringComparison.Ordinal)
            .Replace("{{GkeUseClusterInternalIp}}", B64(gcpGke?.UseClusterInternalIp ?? KubernetesBooleanValues.False), StringComparison.Ordinal)
            // Proxy
            .Replace("{{ProxyHost}}", B64(proxy?.Host), StringComparison.Ordinal)
            .Replace("{{ProxyPort}}", B64(proxy?.Port), StringComparison.Ordinal)
            .Replace("{{ProxyUsername}}", B64(proxy?.Username), StringComparison.Ordinal)
            .Replace("{{ProxyPassword}}", B64(proxy?.Password), StringComparison.Ordinal);
    }

    private static string ResolveNamespace(ScriptContext context, KubernetesApiEndpointDto endpoint)
    {
        if (context?.ActionProperties != null && context.ActionProperties.Count > 0)
            return KubernetesPropertyParser.GetNamespace(context.ActionProperties);

        return endpoint?.Namespace ?? KubernetesDefaultValues.Namespace;
    }
}
