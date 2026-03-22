using System;
using System.Collections.Generic;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Services.DeploymentExecution.Transport;

// ReSharper disable InconsistentNaming

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesApiContextScriptBuilderTests
{
    private readonly KubernetesApiContextScriptBuilder _builder = new();

    private static ScriptContext CreateContext(
        string clusterUrl = "https://k8s.example.com:6443",
        string ns = "default",
        string skipTls = "False",
        string clusterCert = null,
        AccountType? accountType = null,
        string credentialsJson = null,
        ScriptSyntax syntax = ScriptSyntax.Bash,
        Dictionary<string, string> actionProperties = null)
    {
        var endpoint = new EndpointContext
        {
            EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            {
                ClusterUrl = clusterUrl,
                Namespace = ns,
                SkipTlsVerification = skipTls
            })
        };

        if (clusterCert != null)
            endpoint.SetCertificate(EndpointResourceType.ClusterCertificate, clusterCert);

        if (accountType.HasValue && credentialsJson != null)
            endpoint.SetAccountData(accountType.Value, credentialsJson);

        return new ScriptContext { Endpoint = endpoint, Syntax = syntax, ActionProperties = actionProperties };
    }

    private static ScriptContext TokenContext(ScriptSyntax syntax = ScriptSyntax.Bash, string token = "test-token-123")
    {
        var ctx = CreateContext(accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = token }), syntax: syntax);
        return ctx;
    }

    private static ScriptContext UsernamePasswordContext(ScriptSyntax syntax = ScriptSyntax.Bash)
        => CreateContext(accountType: AccountType.UsernamePassword,
            credentialsJson: JsonSerializer.Serialize(new UsernamePasswordCredentials { Username = "admin", Password = "s3cret" }),
            syntax: syntax);

    private static ScriptContext ClientCertContext(ScriptSyntax syntax = ScriptSyntax.Bash)
        => CreateContext(accountType: AccountType.ClientCertificate,
            credentialsJson: JsonSerializer.Serialize(new ClientCertificateCredentials { ClientCertificateData = "LS0tLS1CRUdJTi...", ClientCertificateKeyData = "LS0tLS1CRUdJTi...KEY" }),
            syntax: syntax);

    private static ScriptContext AwsContext(ScriptSyntax syntax = ScriptSyntax.Bash)
        => CreateContext(accountType: AccountType.AmazonWebServicesAccount,
            credentialsJson: JsonSerializer.Serialize(new AwsCredentials { AccessKey = "AKIAIOSFODNN7EXAMPLE", SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY" }),
            syntax: syntax);

    // === Token Auth Tests ===

    [Fact]
    public void WrapWithContext_TokenAuth_Bash_ContainsTokenCredentials()
    {
        var result = _builder.WrapWithContext("kubectl get pods", TokenContext());

        result.ShouldContain("test-token-123");
        result.ShouldContain("Token");
        result.ShouldContain("kubectl get pods");
    }

    [Fact]
    public void WrapWithContext_TokenAuth_PowerShell_ContainsTokenCredentials()
    {
        var result = _builder.WrapWithContext("kubectl get pods", TokenContext(ScriptSyntax.PowerShell));

        result.ShouldContain("test-token-123");
        result.ShouldContain("Token");
        result.ShouldContain("kubectl get pods");
    }

    // === UsernamePassword Auth Tests ===

    [Fact]
    public void WrapWithContext_UsernamePasswordAuth_Bash_ContainsCredentials()
    {
        var result = _builder.WrapWithContext("kubectl get nodes", UsernamePasswordContext());

        result.ShouldContain("admin");
        result.ShouldContain("s3cret");
        result.ShouldContain("UsernamePassword");
    }

    [Fact]
    public void WrapWithContext_UsernamePasswordAuth_PowerShell_ContainsCredentials()
    {
        var result = _builder.WrapWithContext("kubectl get nodes", UsernamePasswordContext(ScriptSyntax.PowerShell));

        result.ShouldContain("admin");
        result.ShouldContain("s3cret");
        result.ShouldContain("UsernamePassword");
    }

    // === ClientCertificate Auth Tests ===

    [Fact]
    public void WrapWithContext_ClientCertAuth_Bash_ContainsCertData()
    {
        var result = _builder.WrapWithContext("kubectl get pods", ClientCertContext());

        result.ShouldContain("LS0tLS1CRUdJTi...");
        result.ShouldContain("LS0tLS1CRUdJTi...KEY");
        result.ShouldContain("ClientCertificate");
    }

    [Fact]
    public void WrapWithContext_ClientCertAuth_PowerShell_ContainsCertData()
    {
        var result = _builder.WrapWithContext("kubectl get pods", ClientCertContext(ScriptSyntax.PowerShell));

        result.ShouldContain("LS0tLS1CRUdJTi...");
        result.ShouldContain("LS0tLS1CRUdJTi...KEY");
        result.ShouldContain("ClientCertificate");
    }

    // === AWS Auth Tests ===

    [Fact]
    public void WrapWithContext_AwsAuth_Bash_ContainsAwsCredentials()
    {
        var result = _builder.WrapWithContext("kubectl get pods", AwsContext());

        result.ShouldContain("AKIAIOSFODNN7EXAMPLE");
        result.ShouldContain("wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
        result.ShouldContain("AmazonWebServicesAccount");
    }

    [Fact]
    public void WrapWithContext_AwsAuth_PowerShell_ContainsAwsCredentials()
    {
        var result = _builder.WrapWithContext("kubectl get pods", AwsContext(ScriptSyntax.PowerShell));

        result.ShouldContain("AKIAIOSFODNN7EXAMPLE");
        result.ShouldContain("wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
        result.ShouldContain("AmazonWebServicesAccount");
    }

    // === Cluster URL Tests ===

    [Fact]
    public void WrapWithContext_ClusterUrl_Bash_ContainsUrl()
    {
        var ctx = TokenContext();
        ctx.Endpoint.EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            { ClusterUrl = "https://my-cluster.example.com:8443", Namespace = "default", SkipTlsVerification = "False" });

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("https://my-cluster.example.com:8443");
    }

    [Fact]
    public void WrapWithContext_ClusterUrl_PowerShell_ContainsUrl()
    {
        var ctx = TokenContext(ScriptSyntax.PowerShell);
        ctx.Endpoint.EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            { ClusterUrl = "https://my-cluster.example.com:8443", Namespace = "default", SkipTlsVerification = "False" });

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("https://my-cluster.example.com:8443");
    }

    // === Namespace Tests ===

    [Fact]
    public void WrapWithContext_CustomNamespace_Bash_ContainsNamespace()
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Squid.Action.KubernetesContainers.Namespace"] = "production"
        };
        var ctx = CreateContext(accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "t" }),
            actionProperties: props);

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("production");
    }

    [Fact]
    public void WrapWithContext_CustomNamespace_PowerShell_ContainsNamespace()
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Squid.Action.KubernetesContainers.Namespace"] = "production"
        };
        var ctx = CreateContext(accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "t" }),
            syntax: ScriptSyntax.PowerShell,
            actionProperties: props);

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("production");
    }

    [Fact]
    public void WrapWithContext_NoActionProperties_FallsBackToEndpointNamespace()
    {
        var ctx = CreateContext(ns: "endpoint-ns", accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "t" }));

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("endpoint-ns");
    }

    [Fact]
    public void WrapWithContext_ActionProperties_OverridesEndpointNamespace()
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Squid.Action.KubernetesContainers.Namespace"] = "action-ns"
        };
        var ctx = CreateContext(ns: "endpoint-ns", accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "t" }),
            actionProperties: props);

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("action-ns");
        result.ShouldNotContain("endpoint-ns");
    }

    // === TLS Skip Tests ===

    [Fact]
    public void WrapWithContext_SkipTls_Bash_ContainsSkipTlsTrue()
    {
        var ctx = CreateContext(skipTls: "True", accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "t" }));

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("True");
    }

    [Fact]
    public void WrapWithContext_SkipTls_PowerShell_ContainsSkipTlsTrue()
    {
        var ctx = CreateContext(skipTls: "True", accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "t" }),
            syntax: ScriptSyntax.PowerShell);

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("True");
    }

    // === Cluster Certificate Tests ===

    [Fact]
    public void WrapWithContext_ClusterCert_Bash_ContainsCertContent()
    {
        var ctx = CreateContext(clusterCert: "MIICpDCCAYwCCQDU+pQ4pHgSpDANBg...", accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "t" }));

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("MIICpDCCAYwCCQDU+pQ4pHgSpDANBg...");
    }

    [Fact]
    public void WrapWithContext_ClusterCert_PowerShell_ContainsCertContent()
    {
        var ctx = CreateContext(clusterCert: "MIICpDCCAYwCCQDU+pQ4pHgSpDANBg...", accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "t" }),
            syntax: ScriptSyntax.PowerShell);

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("MIICpDCCAYwCCQDU+pQ4pHgSpDANBg...");
    }

    // === User Script Embedding Tests ===

    [Fact]
    public void WrapWithContext_UserScript_Bash_ScriptEmbeddedAtEnd()
    {
        var userScript = "kubectl apply -f deployment.yaml\nkubectl rollout status deployment/myapp";

        var result = _builder.WrapWithContext(userScript, TokenContext());

        result.ShouldContain("kubectl apply -f deployment.yaml");
        result.ShouldContain("kubectl rollout status deployment/myapp");
        var contextIndex = result.IndexOf("config use-context", StringComparison.Ordinal);
        var userScriptIndex = result.IndexOf("kubectl apply -f deployment.yaml", StringComparison.Ordinal);
        userScriptIndex.ShouldBeGreaterThan(contextIndex);
    }

    [Fact]
    public void WrapWithContext_UserScript_PowerShell_ScriptEmbeddedAtEnd()
    {
        var result = _builder.WrapWithContext("kubectl apply -f deployment.yaml", TokenContext(ScriptSyntax.PowerShell));

        result.ShouldContain("kubectl apply -f deployment.yaml");
        var contextIndex = result.IndexOf("config use-context", StringComparison.Ordinal);
        var userScriptIndex = result.IndexOf("kubectl apply -f deployment.yaml", StringComparison.Ordinal);
        userScriptIndex.ShouldBeGreaterThan(contextIndex);
    }

    [Fact]
    public void WrapWithContext_EmptyUserScript_Bash_DoesNotThrow()
    {
        var result = _builder.WrapWithContext(string.Empty, TokenContext());

        result.ShouldNotBeNullOrEmpty();
    }

    // === Custom Kubectl Path Tests ===

    [Fact]
    public void WrapWithContext_CustomKubectl_Bash_ContainsCustomPath()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext(), "/usr/local/bin/kubectl-1.28");

        result.ShouldContain("/usr/local/bin/kubectl-1.28");
    }

    [Fact]
    public void WrapWithContext_CustomKubectl_PowerShell_ContainsCustomPath()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext(ScriptSyntax.PowerShell), "C:\\tools\\kubectl.exe");

        result.ShouldContain("C:\\tools\\kubectl.exe");
    }

    [Fact]
    public void WrapWithContext_NullCustomKubectl_Bash_FallsBackToDefault()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext(), null);

        result.ShouldNotBeNullOrEmpty();
    }

    // === Syntax Selection Tests ===

    [Fact]
    public void WrapWithContext_BashSyntax_UsesBashTemplate()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext());

        result.ShouldContain("#!/usr/bin/env bash");
    }

    [Fact]
    public void WrapWithContext_PowerShellSyntax_UsesPowerShellTemplate()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext(ScriptSyntax.PowerShell));

        result.ShouldContain("$ErrorActionPreference");
    }

    // === Null Safety Tests ===

    [Fact]
    public void WrapWithContext_NullEndpoint_DoesNotThrow()
    {
        var ctx = TokenContext();
        ctx.Endpoint.EndpointJson = null;

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void WrapWithContext_NullAccount_DoesNotThrow()
    {
        var ctx = CreateContext();

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void WrapWithContext_NullUserScript_DoesNotThrow()
    {
        var result = _builder.WrapWithContext(null, TokenContext());

        result.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void WrapWithContext_AllNull_DoesNotThrow()
    {
        var ctx = new ScriptContext { Endpoint = new EndpointContext(), Syntax = ScriptSyntax.Bash };

        var result = _builder.WrapWithContext(null, ctx);

        result.ShouldNotBeNullOrEmpty();
    }

    // === Template Completeness Tests ===

    [Fact]
    public void WrapWithContext_Bash_NoUnreplacedPlaceholders()
    {
        var result = _builder.WrapWithContext("kubectl get pods", TokenContext());

        result.ShouldNotContain("{{");
        result.ShouldNotContain("}}");
    }

    [Fact]
    public void WrapWithContext_PowerShell_NoUnreplacedPlaceholders()
    {
        var result = _builder.WrapWithContext("kubectl get pods", TokenContext(ScriptSyntax.PowerShell));

        result.ShouldNotContain("{{");
        result.ShouldNotContain("}}");
    }

    // === Security — Credential Escaping ===

    [Fact]
    public void WrapWithContext_TokenWithDollarSign_Bash_IsEscaped()
    {
        var ctx = CreateContext(accountType: AccountType.Token,
            credentialsJson: JsonSerializer.Serialize(new TokenCredentials { Token = "token$with`special\"chars" }));

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldNotContain("token$with");
        result.ShouldContain("token\\$with");
        result.ShouldContain("\\`special");
        result.ShouldContain("\\\"chars");
    }

    [Fact]
    public void WrapWithContext_PasswordWithSpecialChars_PowerShell_IsEscaped()
    {
        var ctx = CreateContext(accountType: AccountType.UsernamePassword,
            credentialsJson: JsonSerializer.Serialize(new UsernamePasswordCredentials { Username = "admin", Password = "p@ss$word`test\"quote" }),
            syntax: ScriptSyntax.PowerShell);

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("`$word");
        result.ShouldContain("``test");
        result.ShouldContain("`\"quote");
    }

    // === EKS Auth — AwsClusterName/AwsRegion ===

    [Fact]
    public void WrapWithContext_AwsAuth_Bash_ContainsClusterNameAndRegion()
    {
        var endpoint = new EndpointContext
        {
            EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            {
                ClusterUrl = "https://eks.example.com",
                Namespace = "default",
                SkipTlsVerification = "False",
                ProviderType = KubernetesApiEndpointProviderType.AwsEks,
                ProviderConfig = JsonSerializer.Serialize(new KubernetesApiAwsEksConfig { ClusterName = "my-eks-cluster", Region = "us-west-2" })
            })
        };
        endpoint.SetAccountData(AccountType.AmazonWebServicesAccount,
            JsonSerializer.Serialize(new AwsCredentials { AccessKey = "AKIA123", SecretKey = "secret123" }));

        var ctx = new ScriptContext { Endpoint = endpoint, Syntax = ScriptSyntax.Bash };

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("my-eks-cluster");
        result.ShouldContain("us-west-2");
    }

    // === Security — No eval in Bash Template ===

    [Fact]
    public void WrapWithContext_Bash_DoesNotUseEval()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext());

        result.ShouldNotContain("eval ");
    }

    [Fact]
    public void WrapWithContext_Bash_UsesBashArrayForClusterCmd()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext());

        result.ShouldContain("CLUSTER_CMD=(");
        result.ShouldContain("\"${CLUSTER_CMD[@]}\"");
    }

    // === Cert Cleanup — Bash ===

    [Fact]
    public void WrapWithContext_Bash_CleanupTrapRemovesCertFiles()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext());

        result.ShouldContain("CERT_PATH=\"\"");
        result.ShouldContain("CLIENT_CERT_PATH=\"\"");
        result.ShouldContain("CLIENT_KEY_PATH=\"\"");
        result.ShouldContain("rm -f \"$CERT_PATH\"");
        result.ShouldContain("rm -f \"$CLIENT_CERT_PATH\"");
        result.ShouldContain("rm -f \"$CLIENT_KEY_PATH\"");
    }

    // === Cert Cleanup — PowerShell ===

    [Fact]
    public void WrapWithContext_PowerShell_FinallyCleansCertFiles()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext(ScriptSyntax.PowerShell));

        result.ShouldContain("$certPath = $null");
        result.ShouldContain("$clientCertPath = $null");
        result.ShouldContain("$clientKeyPath = $null");
        result.ShouldContain("finally");
    }

    // === Error Handling — Bash ===

    [Fact]
    public void WrapWithContext_Bash_HasErrorHandlingForKubectlCommands()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext());

        result.ShouldContain("kubectl config set-cluster failed");
        result.ShouldContain("kubectl config set-credentials failed");
        result.ShouldContain("kubectl config set-context failed");
        result.ShouldContain("kubectl config use-context failed");
    }

    // === Azure Service Principal Auth Tests ===

    private static ScriptContext AzureServicePrincipalContext(ScriptSyntax syntax = ScriptSyntax.Bash)
    {
        var endpoint = new EndpointContext
        {
            EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            {
                ClusterUrl = "https://aks.example.com",
                Namespace = "default",
                SkipTlsVerification = "False",
                ProviderType = KubernetesApiEndpointProviderType.AzureAks,
                ProviderConfig = JsonSerializer.Serialize(new KubernetesApiAzureAksConfig { ClusterName = "my-aks-cluster", ResourceGroup = "my-rg" })
            })
        };
        endpoint.SetAccountData(AccountType.AzureServicePrincipal,
            JsonSerializer.Serialize(new AzureServicePrincipalCredentials { SubscriptionNumber = "sub-123", ClientId = "client-id-456", TenantId = "tenant-id-789", Key = "sp-secret-key" }));

        return new ScriptContext { Endpoint = endpoint, Syntax = syntax };
    }

    [Fact]
    public void WrapWithContext_AzureServicePrincipal_Bash_ContainsAzLogin()
    {
        var result = _builder.WrapWithContext("echo hi", AzureServicePrincipalContext());

        result.ShouldContain("az login --service-principal");
        result.ShouldContain("client-id-456");
        result.ShouldContain("sp-secret-key");
        result.ShouldContain("tenant-id-789");
    }

    [Fact]
    public void WrapWithContext_AzureServicePrincipal_Bash_ContainsAksGetCredentials()
    {
        var result = _builder.WrapWithContext("echo hi", AzureServicePrincipalContext());

        result.ShouldContain("az aks get-credentials");
        result.ShouldContain("my-aks-cluster");
        result.ShouldContain("my-rg");
    }

    [Fact]
    public void WrapWithContext_AzureServicePrincipal_PowerShell_ContainsAzLogin()
    {
        var result = _builder.WrapWithContext("echo hi", AzureServicePrincipalContext(ScriptSyntax.PowerShell));

        result.ShouldContain("az login --service-principal");
        result.ShouldContain("client-id-456");
        result.ShouldContain("tenant-id-789");
    }

    [Fact]
    public void WrapWithContext_AzureServicePrincipal_Bash_SetsSubscription()
    {
        var result = _builder.WrapWithContext("echo hi", AzureServicePrincipalContext());

        result.ShouldContain("az account set --subscription");
        result.ShouldContain("sub-123");
    }

    [Fact]
    public void WrapWithContext_AzureServicePrincipal_Bash_EscapesSpecialCharsInKey()
    {
        var endpoint = new EndpointContext
        {
            EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            {
                ClusterUrl = "https://aks.example.com", Namespace = "default", SkipTlsVerification = "False",
                ProviderType = KubernetesApiEndpointProviderType.AzureAks,
                ProviderConfig = JsonSerializer.Serialize(new KubernetesApiAzureAksConfig { ClusterName = "cluster", ResourceGroup = "rg" })
            })
        };
        endpoint.SetAccountData(AccountType.AzureServicePrincipal,
            JsonSerializer.Serialize(new AzureServicePrincipalCredentials { SubscriptionNumber = "sub", ClientId = "cid", TenantId = "tid", Key = "key$with`special" }));

        var result = _builder.WrapWithContext("echo hi", new ScriptContext { Endpoint = endpoint, Syntax = ScriptSyntax.Bash });

        result.ShouldContain("key\\$with");
        result.ShouldContain("\\`special");
    }

    // === Azure OIDC Auth Tests ===

    private static ScriptContext AzureOidcContext(ScriptSyntax syntax = ScriptSyntax.Bash)
    {
        var endpoint = new EndpointContext
        {
            EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            {
                ClusterUrl = "https://aks-oidc.example.com",
                Namespace = "default",
                SkipTlsVerification = "False",
                ProviderType = KubernetesApiEndpointProviderType.AzureAks,
                ProviderConfig = JsonSerializer.Serialize(new KubernetesApiAzureAksConfig { ClusterName = "my-oidc-cluster", ResourceGroup = "my-oidc-rg" })
            })
        };
        endpoint.SetAccountData(AccountType.AzureOidc,
            JsonSerializer.Serialize(new AzureOidcCredentials { SubscriptionNumber = "oidc-sub-123", ClientId = "oidc-client-456", TenantId = "oidc-tenant-789", Audience = "api://AzureADTokenExchange" }));

        return new ScriptContext { Endpoint = endpoint, Syntax = syntax };
    }

    [Fact]
    public void WrapWithContext_AzureOidc_Bash_ContainsFederatedTokenLogin()
    {
        var result = _builder.WrapWithContext("echo hi", AzureOidcContext());

        result.ShouldContain("--federated-token");
        result.ShouldContain("oidc-client-456");
        result.ShouldContain("oidc-tenant-789");
        result.ShouldContain("api://AzureADTokenExchange");
    }

    [Fact]
    public void WrapWithContext_AzureOidc_PowerShell_ContainsFederatedTokenLogin()
    {
        var result = _builder.WrapWithContext("echo hi", AzureOidcContext(ScriptSyntax.PowerShell));

        result.ShouldContain("--federated-token");
        result.ShouldContain("oidc-client-456");
        result.ShouldContain("oidc-tenant-789");
    }

    // === Azure Config Dir Isolation Tests ===

    [Theory]
    [InlineData("AzureServicePrincipal")]
    [InlineData("AzureOidc")]
    public void WrapWithContext_AzureAuth_Bash_SetsAzureConfigDir(string authType)
    {
        var ctx = authType == "AzureServicePrincipal"
            ? AzureServicePrincipalContext()
            : AzureOidcContext();

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("AZURE_CONFIG_DIR");
        result.ShouldContain("mktemp -d /tmp/azure-cli-");
    }

    [Theory]
    [InlineData("AzureServicePrincipal")]
    [InlineData("AzureOidc")]
    public void WrapWithContext_AzureAuth_PowerShell_SetsAzureConfigDir(string authType)
    {
        var ctx = authType == "AzureServicePrincipal"
            ? AzureServicePrincipalContext(ScriptSyntax.PowerShell)
            : AzureOidcContext(ScriptSyntax.PowerShell);

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("AZURE_CONFIG_DIR");
        result.ShouldContain("azure-cli-");
    }

    // === kubelogin Integration Tests ===

    [Theory]
    [InlineData("AzureServicePrincipal")]
    [InlineData("AzureOidc")]
    public void WrapWithContext_AzureAuth_Bash_ContainsKubeloginConvert(string authType)
    {
        var ctx = authType == "AzureServicePrincipal"
            ? AzureServicePrincipalContext()
            : AzureOidcContext();

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("kubelogin convert-kubeconfig");
        result.ShouldContain("-l azurecli");
    }

    [Theory]
    [InlineData("AzureServicePrincipal")]
    [InlineData("AzureOidc")]
    public void WrapWithContext_AzureAuth_PowerShell_ContainsKubeloginConvert(string authType)
    {
        var ctx = authType == "AzureServicePrincipal"
            ? AzureServicePrincipalContext(ScriptSyntax.PowerShell)
            : AzureOidcContext(ScriptSyntax.PowerShell);

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldContain("kubelogin convert-kubeconfig");
        result.ShouldContain("-l azurecli");
    }

    [Theory]
    [InlineData("AzureServicePrincipal")]
    [InlineData("AzureOidc")]
    public void WrapWithContext_AzureAuth_Bash_NoUnreplacedPlaceholders(string authType)
    {
        var ctx = authType == "AzureServicePrincipal"
            ? AzureServicePrincipalContext()
            : AzureOidcContext();

        var result = _builder.WrapWithContext("echo hi", ctx);

        result.ShouldNotContain("{{");
        result.ShouldNotContain("}}");
    }

    // === GCP Auth Tests ===

    private static ScriptContext GcpContext(ScriptSyntax syntax = ScriptSyntax.Bash, string zone = "", string region = "", string useInternalIp = "False")
    {
        var endpoint = new EndpointContext
        {
            EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            {
                ClusterUrl = "https://gke.example.com",
                Namespace = "default",
                SkipTlsVerification = "False",
                ProviderType = KubernetesApiEndpointProviderType.GcpGke,
                ProviderConfig = JsonSerializer.Serialize(new KubernetesApiGcpGkeConfig { ClusterName = "my-gke-cluster", Project = "my-gcp-project", Zone = zone, Region = region, UseClusterInternalIp = useInternalIp })
            })
        };
        endpoint.SetAccountData(AccountType.GoogleCloudAccount,
            JsonSerializer.Serialize(new GcpCredentials { JsonKey = "{\"type\":\"service_account\",\"project_id\":\"test\"}" }));

        return new ScriptContext { Endpoint = endpoint, Syntax = syntax };
    }

    [Fact]
    public void WrapWithContext_Gcp_Bash_ContainsGcloudActivateServiceAccount()
    {
        var result = _builder.WrapWithContext("echo hi", GcpContext(zone: "us-central1-a"));

        result.ShouldContain("gcloud auth activate-service-account");
        result.ShouldContain("{\\\"type\\\":\\\"service_account\\\"");
    }

    [Fact]
    public void WrapWithContext_Gcp_Bash_ContainsGetCredentials()
    {
        var result = _builder.WrapWithContext("echo hi", GcpContext(zone: "us-central1-a"));

        result.ShouldContain("gcloud container clusters get-credentials");
        result.ShouldContain("my-gke-cluster");
        result.ShouldContain("my-gcp-project");
    }

    [Fact]
    public void WrapWithContext_Gcp_PowerShell_ContainsGcloudAuth()
    {
        var result = _builder.WrapWithContext("echo hi", GcpContext(ScriptSyntax.PowerShell, zone: "us-central1-a"));

        result.ShouldContain("gcloud auth activate-service-account");
        result.ShouldContain("my-gke-cluster");
    }

    [Fact]
    public void WrapWithContext_Gcp_ZoneBasedCluster_UsesZoneFlag()
    {
        var result = _builder.WrapWithContext("echo hi", GcpContext(zone: "us-central1-a"));

        result.ShouldContain("us-central1-a");
    }

    [Fact]
    public void WrapWithContext_Gcp_RegionBasedCluster_UsesRegionFlag()
    {
        var result = _builder.WrapWithContext("echo hi", GcpContext(region: "us-central1"));

        result.ShouldContain("us-central1");
    }

    [Fact]
    public void WrapWithContext_Gcp_InternalIp_ContainsInternalIpFlag()
    {
        var result = _builder.WrapWithContext("echo hi", GcpContext(zone: "us-central1-a", useInternalIp: "True"));

        result.ShouldContain("--internal-ip");
    }

    [Fact]
    public void WrapWithContext_Gcp_JsonKeyWithSpecialChars_EscapedProperly()
    {
        var result = _builder.WrapWithContext("echo hi", GcpContext(zone: "us-central1-a"));

        result.ShouldNotContain("{{GcpJsonKey}}");
    }

    [Fact]
    public void WrapWithContext_Gcp_Bash_NoUnreplacedPlaceholders()
    {
        var result = _builder.WrapWithContext("echo hi", GcpContext(zone: "us-central1-a"));

        result.ShouldNotContain("{{");
        result.ShouldNotContain("}}");
    }

    // === AWS OIDC Auth Tests ===

    private static ScriptContext AwsOidcContext(ScriptSyntax syntax = ScriptSyntax.Bash)
    {
        var endpoint = new EndpointContext
        {
            EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            {
                ClusterUrl = "https://eks-oidc.example.com",
                Namespace = "default",
                SkipTlsVerification = "False",
                ProviderType = KubernetesApiEndpointProviderType.AwsEks,
                ProviderConfig = JsonSerializer.Serialize(new KubernetesApiAwsEksConfig { ClusterName = "my-oidc-eks-cluster", Region = "us-east-1" })
            })
        };
        endpoint.SetAccountData(AccountType.AmazonWebServicesOidcAccount,
            JsonSerializer.Serialize(new AwsOidcCredentials { RoleArn = "arn:aws:iam::123456789:role/my-role", WebIdentityToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9" }));

        return new ScriptContext { Endpoint = endpoint, Syntax = syntax };
    }

    [Fact]
    public void WrapWithContext_AwsOidc_Bash_ContainsRoleArn()
    {
        var result = _builder.WrapWithContext("echo hi", AwsOidcContext());

        result.ShouldContain("arn:aws:iam::123456789:role/my-role");
        result.ShouldContain("--role-arn");
    }

    [Fact]
    public void WrapWithContext_AwsOidc_Bash_SetsWebIdentityTokenFile()
    {
        var result = _builder.WrapWithContext("echo hi", AwsOidcContext());

        result.ShouldContain("AWS_WEB_IDENTITY_TOKEN_FILE");
        result.ShouldContain("eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9");
    }

    [Fact]
    public void WrapWithContext_AwsOidc_PowerShell_ContainsRoleArn()
    {
        var result = _builder.WrapWithContext("echo hi", AwsOidcContext(ScriptSyntax.PowerShell));

        result.ShouldContain("arn:aws:iam::123456789:role/my-role");
        result.ShouldContain("--role-arn");
    }

    [Fact]
    public void WrapWithContext_AwsOidc_Bash_NoUnreplacedPlaceholders()
    {
        var result = _builder.WrapWithContext("echo hi", AwsOidcContext());

        result.ShouldNotContain("{{");
        result.ShouldNotContain("}}");
    }

    // === Proxy Tests ===

    private static ScriptContext ProxyContext(ScriptSyntax syntax = ScriptSyntax.Bash, string proxyUser = null, string proxyPass = null)
    {
        var endpoint = new EndpointContext
        {
            EndpointJson = JsonSerializer.Serialize(new KubernetesApiEndpointDto
            {
                ClusterUrl = "https://k8s.example.com",
                Namespace = "default",
                SkipTlsVerification = "False",
                Proxy = new KubernetesApiEndpointProxyConfig { Host = "proxy.example.com", Port = "8080", Username = proxyUser, Password = proxyPass }
            })
        };
        endpoint.SetAccountData(AccountType.Token,
            JsonSerializer.Serialize(new TokenCredentials { Token = "t" }));

        return new ScriptContext { Endpoint = endpoint, Syntax = syntax };
    }

    [Fact]
    public void WrapWithContext_ProxyConfigured_Bash_SetsHttpsProxy()
    {
        var result = _builder.WrapWithContext("echo hi", ProxyContext());

        result.ShouldContain("HTTPS_PROXY");
        result.ShouldContain("proxy.example.com");
        result.ShouldContain("8080");
    }

    [Fact]
    public void WrapWithContext_ProxyWithAuth_Bash_IncludesCredentialsInUrl()
    {
        var result = _builder.WrapWithContext("echo hi", ProxyContext(proxyUser: "puser", proxyPass: "ppass"));

        result.ShouldContain("puser");
        result.ShouldContain("ppass");
    }

    [Fact]
    public void WrapWithContext_NoProxy_Bash_ProxyHostEmpty()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext());

        result.ShouldContain("PROXY_HOST=\"\"");
    }

    [Fact]
    public void WrapWithContext_ProxyConfigured_PowerShell_SetsEnvVar()
    {
        var result = _builder.WrapWithContext("echo hi", ProxyContext(ScriptSyntax.PowerShell));

        result.ShouldContain("HTTPS_PROXY");
        result.ShouldContain("proxy.example.com");
    }

    // === Kubeconfig Isolation Tests ===

    [Fact]
    public void WrapWithContext_Bash_SetsKubeconfigEnv()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext());

        result.ShouldContain("KUBECONFIG");
    }

    [Fact]
    public void WrapWithContext_PowerShell_SetsKubeconfigEnv()
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext(ScriptSyntax.PowerShell));

        result.ShouldContain("KUBECONFIG");
    }

    // === File-Based Credential Passing ===

    [Theory]
    [InlineData(ScriptSyntax.Bash)]
    [InlineData(ScriptSyntax.PowerShell)]
    public void WrapWithContext_Token_WritesToTempFileAndReadsBack(ScriptSyntax syntax)
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext(syntax));

        if (syntax == ScriptSyntax.Bash)
        {
            result.ShouldContain("mktemp /tmp/cred-token-");
            result.ShouldContain("cat \"$CRED_FILE\"");
        }
        else
        {
            result.ShouldContain("cred-token-");
            result.ShouldContain("WriteAllText($credFile");
            result.ShouldContain("ReadAllText($credFile)");
        }
    }

    [Theory]
    [InlineData(ScriptSyntax.Bash)]
    [InlineData(ScriptSyntax.PowerShell)]
    public void WrapWithContext_Password_WritesToTempFileAndReadsBack(ScriptSyntax syntax)
    {
        var result = _builder.WrapWithContext("echo hi", UsernamePasswordContext(syntax));

        if (syntax == ScriptSyntax.Bash)
        {
            result.ShouldContain("mktemp /tmp/cred-pass-");
            result.ShouldContain("cat \"$CRED_FILE\"");
        }
        else
        {
            result.ShouldContain("cred-pass-");
            result.ShouldContain("WriteAllText($credFile");
            result.ShouldContain("ReadAllText($credFile)");
        }
    }

    [Theory]
    [InlineData(ScriptSyntax.Bash)]
    [InlineData(ScriptSyntax.PowerShell)]
    public void WrapWithContext_AwsSecretKey_WritesToTempFileAndReadsBack(ScriptSyntax syntax)
    {
        var result = _builder.WrapWithContext("echo hi", AwsContext(syntax));

        if (syntax == ScriptSyntax.Bash)
        {
            result.ShouldContain("mktemp /tmp/cred-aws-");
            result.ShouldContain("cat \"$CRED_FILE\"");
        }
        else
        {
            result.ShouldContain("cred-aws-");
            result.ShouldContain("WriteAllText($credFile");
            result.ShouldContain("ReadAllText($credFile)");
        }
    }

    [Theory]
    [InlineData(ScriptSyntax.Bash)]
    [InlineData(ScriptSyntax.PowerShell)]
    public void WrapWithContext_Token_CredFileCleanedUp(ScriptSyntax syntax)
    {
        var result = _builder.WrapWithContext("echo hi", TokenContext(syntax));

        if (syntax == ScriptSyntax.Bash)
            result.ShouldContain("rm -f \"$CRED_FILE\"");
        else
            result.ShouldContain("$credFile)");
    }
}
