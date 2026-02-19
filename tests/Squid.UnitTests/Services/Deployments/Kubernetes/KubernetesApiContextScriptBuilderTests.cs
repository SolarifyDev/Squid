using System;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesApiContextScriptBuilderTests
{
    private readonly KubernetesApiContextScriptBuilder _builder = new();

    private static KubernetesApiEndpointDto CreateEndpoint(
        string clusterUrl = "https://k8s.example.com:6443",
        string ns = "default",
        string skipTls = "False",
        string clusterCert = null) => new()
    {
        ClusterUrl = clusterUrl,
        Namespace = ns,
        SkipTlsVerification = skipTls,
        ClusterCertificate = clusterCert
    };

    private static DeploymentAccount CreateTokenAccount(string token = "test-token-123") => new()
    {
        AccountType = AccountType.Token,
        Token = token
    };

    private static DeploymentAccount CreateUsernamePasswordAccount(
        string username = "admin",
        string password = "s3cret") => new()
    {
        AccountType = AccountType.UsernamePassword,
        Username = username,
        Password = password
    };

    private static DeploymentAccount CreateClientCertAccount(
        string certData = "LS0tLS1CRUdJTi...",
        string keyData = "LS0tLS1CRUdJTi...KEY") => new()
    {
        AccountType = AccountType.ClientCertificate,
        ClientCertificateData = certData,
        ClientCertificateKeyData = keyData
    };

    private static DeploymentAccount CreateAwsAccount(
        string accessKey = "AKIAIOSFODNN7EXAMPLE",
        string secretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY") => new()
    {
        AccountType = AccountType.AmazonWebServicesAccount,
        AccessKey = accessKey,
        SecretKey = secretKey
    };

    // === Token Auth Tests ===

    [Fact]
    public void WrapWithContext_TokenAuth_Bash_ContainsTokenCredentials()
    {
        var result = _builder.WrapWithContext(
            "kubectl get pods",
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldContain("test-token-123");
        result.ShouldContain("Token");
        result.ShouldContain("kubectl get pods");
    }

    [Fact]
    public void WrapWithContext_TokenAuth_PowerShell_ContainsTokenCredentials()
    {
        var result = _builder.WrapWithContext(
            "kubectl get pods",
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldContain("test-token-123");
        result.ShouldContain("Token");
        result.ShouldContain("kubectl get pods");
    }

    // === UsernamePassword Auth Tests ===

    [Fact]
    public void WrapWithContext_UsernamePasswordAuth_Bash_ContainsCredentials()
    {
        var result = _builder.WrapWithContext(
            "kubectl get nodes",
            CreateEndpoint(),
            CreateUsernamePasswordAccount(),
            ScriptSyntax.Bash);

        result.ShouldContain("admin");
        result.ShouldContain("s3cret");
        result.ShouldContain("UsernamePassword");
    }

    [Fact]
    public void WrapWithContext_UsernamePasswordAuth_PowerShell_ContainsCredentials()
    {
        var result = _builder.WrapWithContext(
            "kubectl get nodes",
            CreateEndpoint(),
            CreateUsernamePasswordAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldContain("admin");
        result.ShouldContain("s3cret");
        result.ShouldContain("UsernamePassword");
    }

    // === ClientCertificate Auth Tests ===

    [Fact]
    public void WrapWithContext_ClientCertAuth_Bash_ContainsCertData()
    {
        var result = _builder.WrapWithContext(
            "kubectl get pods",
            CreateEndpoint(),
            CreateClientCertAccount(),
            ScriptSyntax.Bash);

        result.ShouldContain("LS0tLS1CRUdJTi...");
        result.ShouldContain("LS0tLS1CRUdJTi...KEY");
        result.ShouldContain("ClientCertificate");
    }

    [Fact]
    public void WrapWithContext_ClientCertAuth_PowerShell_ContainsCertData()
    {
        var result = _builder.WrapWithContext(
            "kubectl get pods",
            CreateEndpoint(),
            CreateClientCertAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldContain("LS0tLS1CRUdJTi...");
        result.ShouldContain("LS0tLS1CRUdJTi...KEY");
        result.ShouldContain("ClientCertificate");
    }

    // === AWS Auth Tests ===

    [Fact]
    public void WrapWithContext_AwsAuth_Bash_ContainsAwsCredentials()
    {
        var result = _builder.WrapWithContext(
            "kubectl get pods",
            CreateEndpoint(),
            CreateAwsAccount(),
            ScriptSyntax.Bash);

        result.ShouldContain("AKIAIOSFODNN7EXAMPLE");
        result.ShouldContain("wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
        result.ShouldContain("AmazonWebServicesAccount");
    }

    [Fact]
    public void WrapWithContext_AwsAuth_PowerShell_ContainsAwsCredentials()
    {
        var result = _builder.WrapWithContext(
            "kubectl get pods",
            CreateEndpoint(),
            CreateAwsAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldContain("AKIAIOSFODNN7EXAMPLE");
        result.ShouldContain("wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
        result.ShouldContain("AmazonWebServicesAccount");
    }

    // === Cluster URL Tests ===

    [Fact]
    public void WrapWithContext_ClusterUrl_Bash_ContainsUrl()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(clusterUrl: "https://my-cluster.example.com:8443"),
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldContain("https://my-cluster.example.com:8443");
    }

    [Fact]
    public void WrapWithContext_ClusterUrl_PowerShell_ContainsUrl()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(clusterUrl: "https://my-cluster.example.com:8443"),
            CreateTokenAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldContain("https://my-cluster.example.com:8443");
    }

    // === Namespace Tests ===

    [Fact]
    public void WrapWithContext_CustomNamespace_Bash_ContainsNamespace()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(ns: "production"),
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldContain("production");
    }

    [Fact]
    public void WrapWithContext_CustomNamespace_PowerShell_ContainsNamespace()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(ns: "production"),
            CreateTokenAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldContain("production");
    }

    // === TLS Skip Tests ===

    [Fact]
    public void WrapWithContext_SkipTls_Bash_ContainsSkipTlsTrue()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(skipTls: "True"),
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldContain("True");
    }

    [Fact]
    public void WrapWithContext_SkipTls_PowerShell_ContainsSkipTlsTrue()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(skipTls: "True"),
            CreateTokenAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldContain("True");
    }

    // === Cluster Certificate Tests ===

    [Fact]
    public void WrapWithContext_ClusterCert_Bash_ContainsCertContent()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(clusterCert: "MIICpDCCAYwCCQDU+pQ4pHgSpDANBg..."),
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldContain("MIICpDCCAYwCCQDU+pQ4pHgSpDANBg...");
    }

    [Fact]
    public void WrapWithContext_ClusterCert_PowerShell_ContainsCertContent()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(clusterCert: "MIICpDCCAYwCCQDU+pQ4pHgSpDANBg..."),
            CreateTokenAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldContain("MIICpDCCAYwCCQDU+pQ4pHgSpDANBg...");
    }

    // === User Script Embedding Tests ===

    [Fact]
    public void WrapWithContext_UserScript_Bash_ScriptEmbeddedAtEnd()
    {
        var userScript = "kubectl apply -f deployment.yaml\nkubectl rollout status deployment/myapp";

        var result = _builder.WrapWithContext(
            userScript,
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldContain("kubectl apply -f deployment.yaml");
        result.ShouldContain("kubectl rollout status deployment/myapp");
        // User script should be after context setup
        var contextIndex = result.IndexOf("config use-context", StringComparison.Ordinal);
        var userScriptIndex = result.IndexOf("kubectl apply -f deployment.yaml", StringComparison.Ordinal);
        userScriptIndex.ShouldBeGreaterThan(contextIndex);
    }

    [Fact]
    public void WrapWithContext_UserScript_PowerShell_ScriptEmbeddedAtEnd()
    {
        var userScript = "kubectl apply -f deployment.yaml";

        var result = _builder.WrapWithContext(
            userScript,
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldContain("kubectl apply -f deployment.yaml");
        var contextIndex = result.IndexOf("config use-context", StringComparison.Ordinal);
        var userScriptIndex = result.IndexOf("kubectl apply -f deployment.yaml", StringComparison.Ordinal);
        userScriptIndex.ShouldBeGreaterThan(contextIndex);
    }

    [Fact]
    public void WrapWithContext_EmptyUserScript_Bash_DoesNotThrow()
    {
        var result = _builder.WrapWithContext(
            string.Empty,
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldNotBeNullOrEmpty();
    }

    // === Custom Kubectl Path Tests ===

    [Fact]
    public void WrapWithContext_CustomKubectl_Bash_ContainsCustomPath()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.Bash,
            "/usr/local/bin/kubectl-1.28");

        result.ShouldContain("/usr/local/bin/kubectl-1.28");
    }

    [Fact]
    public void WrapWithContext_CustomKubectl_PowerShell_ContainsCustomPath()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.PowerShell,
            "C:\\tools\\kubectl.exe");

        result.ShouldContain("C:\\tools\\kubectl.exe");
    }

    [Fact]
    public void WrapWithContext_NullCustomKubectl_Bash_FallsBackToDefault()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.Bash,
            null);

        // The template should have empty KubectlExe, which the script handles by defaulting to "kubectl"
        result.ShouldNotBeNullOrEmpty();
    }

    // === Syntax Selection Tests ===

    [Fact]
    public void WrapWithContext_BashSyntax_UsesBashTemplate()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldContain("#!/bin/bash");
    }

    [Fact]
    public void WrapWithContext_PowerShellSyntax_UsesPowerShellTemplate()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldContain("$ErrorActionPreference");
    }

    // === Null Safety Tests ===

    [Fact]
    public void WrapWithContext_NullEndpoint_DoesNotThrow()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            null,
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void WrapWithContext_NullAccount_DoesNotThrow()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(),
            null,
            ScriptSyntax.Bash);

        result.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void WrapWithContext_NullUserScript_DoesNotThrow()
    {
        var result = _builder.WrapWithContext(
            null,
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void WrapWithContext_AllNull_DoesNotThrow()
    {
        var result = _builder.WrapWithContext(
            null,
            null,
            null,
            ScriptSyntax.Bash);

        result.ShouldNotBeNullOrEmpty();
    }

    // === Template Completeness Tests ===

    [Fact]
    public void WrapWithContext_Bash_NoUnreplacedPlaceholders()
    {
        var result = _builder.WrapWithContext(
            "kubectl get pods",
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldNotContain("{{");
        result.ShouldNotContain("}}");
    }

    [Fact]
    public void WrapWithContext_PowerShell_NoUnreplacedPlaceholders()
    {
        var result = _builder.WrapWithContext(
            "kubectl get pods",
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldNotContain("{{");
        result.ShouldNotContain("}}");
    }

    // === Kubeconfig Isolation Tests ===

    [Fact]
    public void WrapWithContext_Bash_SetsKubeconfigEnv()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.Bash);

        result.ShouldContain("KUBECONFIG");
    }

    [Fact]
    public void WrapWithContext_PowerShell_SetsKubeconfigEnv()
    {
        var result = _builder.WrapWithContext(
            "echo hi",
            CreateEndpoint(),
            CreateTokenAccount(),
            ScriptSyntax.PowerShell);

        result.ShouldContain("KUBECONFIG");
    }
}
