using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesEndpointVariableContributorTests
{
    private readonly KubernetesEndpointVariableContributor _contributor = new();

    private static string MakeEndpointJson(
        string clusterUrl = "https://k8s.example.com:6443",
        string ns = "default",
        string skipTls = "False",
        string accountId = "42",
        string clusterCert = null) =>
        JsonSerializer.Serialize(new KubernetesEndpointDto
        {
            CommunicationStyle = "Kubernetes",
            ClusterUrl = clusterUrl,
            Namespace = ns,
            SkipTlsVerification = skipTls,
            AccountId = accountId,
            ClusterCertificate = clusterCert
        });

    private static DeploymentAccount CreateTokenAccount() => new()
    {
        AccountType = AccountType.Token,
        Token = "test-token-123"
    };

    private static DeploymentAccount CreateUsernamePasswordAccount() => new()
    {
        AccountType = AccountType.UsernamePassword,
        Username = "admin",
        Password = "s3cret"
    };

    private static DeploymentAccount CreateClientCertAccount() => new()
    {
        AccountType = AccountType.ClientCertificate,
        ClientCertificateData = "LS0tLS1CRUdJTi...",
        ClientCertificateKeyData = "LS0tLS1CRUdJTi...KEY"
    };

    private static DeploymentAccount CreateAwsAccount() => new()
    {
        AccountType = AccountType.AmazonWebServicesAccount,
        AccessKey = "AKIAIOSFODNN7EXAMPLE",
        SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
    };

    // === CanHandle ===

    [Fact]
    public void CanHandle_Kubernetes_ReturnsTrue()
    {
        _contributor.CanHandle("Kubernetes").ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_CaseInsensitive_ReturnsTrue()
    {
        _contributor.CanHandle("kubernetes").ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_UpperCase_ReturnsTrue()
    {
        _contributor.CanHandle("KUBERNETES").ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_Ssh_ReturnsFalse()
    {
        _contributor.CanHandle("Ssh").ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_Empty_ReturnsFalse()
    {
        _contributor.CanHandle(string.Empty).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_Null_ReturnsFalse()
    {
        _contributor.CanHandle(null).ShouldBeFalse();
    }

    // === ParseAccountId ===

    [Fact]
    public void ParseAccountId_ValidJson_ReturnsAccountId()
    {
        var json = MakeEndpointJson(accountId: "42");
        _contributor.ParseAccountId(json).ShouldBe(42);
    }

    [Fact]
    public void ParseAccountId_NoAccountId_ReturnsNull()
    {
        var json = MakeEndpointJson(accountId: null);
        _contributor.ParseAccountId(json).ShouldBeNull();
    }

    [Fact]
    public void ParseAccountId_NonNumericAccountId_ReturnsNull()
    {
        var json = MakeEndpointJson(accountId: "abc");
        _contributor.ParseAccountId(json).ShouldBeNull();
    }

    [Fact]
    public void ParseAccountId_EmptyAccountId_ReturnsNull()
    {
        var json = MakeEndpointJson(accountId: "");
        _contributor.ParseAccountId(json).ShouldBeNull();
    }

    [Fact]
    public void ParseAccountId_InvalidJson_ReturnsNull()
    {
        _contributor.ParseAccountId("not-json").ShouldBeNull();
    }

    [Fact]
    public void ParseAccountId_EmptyString_ReturnsNull()
    {
        _contributor.ParseAccountId(string.Empty).ShouldBeNull();
    }

    [Fact]
    public void ParseAccountId_Null_ReturnsNull()
    {
        _contributor.ParseAccountId(null).ShouldBeNull();
    }

    // === ContributeVariables — count & all names ===

    [Fact]
    public void ContributeVariables_ValidEndpoint_Returns15Variables()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateTokenAccount());

        vars.Count.ShouldBe(15);
    }

    [Fact]
    public void ContributeVariables_AllExpectedVariableNamesPresent()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateTokenAccount());
        var names = vars.Select(v => v.Name).ToList();

        names.ShouldContain("Squid.Action.Kubernetes.ClusterUrl");
        names.ShouldContain("Squid.Account.AccountType");
        names.ShouldContain("Squid.Account.Token");
        names.ShouldContain("Squid.Account.Username");
        names.ShouldContain("Squid.Account.Password");
        names.ShouldContain("Squid.Account.ClientCertificateData");
        names.ShouldContain("Squid.Account.ClientCertificateKeyData");
        names.ShouldContain("Squid.Account.AccessKey");
        names.ShouldContain("Squid.Account.SecretKey");
        names.ShouldContain("Squid.Action.Kubernetes.SkipTlsVerification");
        names.ShouldContain("Squid.Action.Kubernetes.Namespace");
        names.ShouldContain("Squid.Action.Kubernetes.ClusterCertificate");
        names.ShouldContain("Squid.Action.Script.SuppressEnvironmentLogging");
        names.ShouldContain("Squid.Action.Kubernetes.OutputKubectlVersion");
        names.ShouldContain("SquidPrintEvaluatedVariables");
    }

    // === ContributeVariables — endpoint field values ===

    [Fact]
    public void ContributeVariables_ClusterUrl_MappedCorrectly()
    {
        var json = MakeEndpointJson(clusterUrl: "https://my-cluster:6443");
        var vars = _contributor.ContributeVariables(json, CreateTokenAccount());

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.ClusterUrl" && v.Value == "https://my-cluster:6443");
    }

    [Fact]
    public void ContributeVariables_Namespace_MappedCorrectly()
    {
        var json = MakeEndpointJson(ns: "production");
        var vars = _contributor.ContributeVariables(json, CreateTokenAccount());

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.Namespace" && v.Value == "production");
    }

    [Fact]
    public void ContributeVariables_SkipTls_MappedCorrectly()
    {
        var json = MakeEndpointJson(skipTls: "True");
        var vars = _contributor.ContributeVariables(json, CreateTokenAccount());

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.SkipTlsVerification" && v.Value == "True");
    }

    [Fact]
    public void ContributeVariables_ClusterCertificate_MappedCorrectly()
    {
        var json = MakeEndpointJson(clusterCert: "MIICpDCCAYwCCQ...");
        var vars = _contributor.ContributeVariables(json, CreateTokenAccount());

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.ClusterCertificate" && v.Value == "MIICpDCCAYwCCQ...");
    }

    // === ContributeVariables — Token account ===

    [Fact]
    public void ContributeVariables_TokenAccount_AccountTypeMapped()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateTokenAccount());

        vars.ShouldContain(v => v.Name == "Squid.Account.AccountType" && v.Value == "Token");
    }

    [Fact]
    public void ContributeVariables_TokenAccount_TokenMapped()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateTokenAccount());

        vars.ShouldContain(v => v.Name == "Squid.Account.Token" && v.Value == "test-token-123");
    }

    // === ContributeVariables — UsernamePassword account ===

    [Fact]
    public void ContributeVariables_UsernamePasswordAccount_AccountTypeMapped()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateUsernamePasswordAccount());

        vars.ShouldContain(v => v.Name == "Squid.Account.AccountType" && v.Value == "UsernamePassword");
    }

    [Fact]
    public void ContributeVariables_UsernamePasswordAccount_UsernameMapped()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateUsernamePasswordAccount());

        vars.ShouldContain(v => v.Name == "Squid.Account.Username" && v.Value == "admin");
    }

    [Fact]
    public void ContributeVariables_UsernamePasswordAccount_PasswordMapped()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateUsernamePasswordAccount());

        vars.ShouldContain(v => v.Name == "Squid.Account.Password" && v.Value == "s3cret");
    }

    // === ContributeVariables — ClientCertificate account ===

    [Fact]
    public void ContributeVariables_ClientCertAccount_AccountTypeMapped()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateClientCertAccount());

        vars.ShouldContain(v => v.Name == "Squid.Account.AccountType" && v.Value == "ClientCertificate");
    }

    [Fact]
    public void ContributeVariables_ClientCertAccount_CertDataMapped()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateClientCertAccount());

        vars.ShouldContain(v => v.Name == "Squid.Account.ClientCertificateData" && v.Value == "LS0tLS1CRUdJTi...");
    }

    [Fact]
    public void ContributeVariables_ClientCertAccount_KeyDataMapped()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateClientCertAccount());

        vars.ShouldContain(v => v.Name == "Squid.Account.ClientCertificateKeyData" && v.Value == "LS0tLS1CRUdJTi...KEY");
    }

    // === ContributeVariables — AWS account ===

    [Fact]
    public void ContributeVariables_AwsAccount_AccountTypeMapped()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateAwsAccount());

        vars.ShouldContain(v => v.Name == "Squid.Account.AccountType" && v.Value == "AmazonWebServicesAccount");
    }

    [Fact]
    public void ContributeVariables_AwsAccount_AccessKeyMapped()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateAwsAccount());

        vars.ShouldContain(v => v.Name == "Squid.Account.AccessKey" && v.Value == "AKIAIOSFODNN7EXAMPLE");
    }

    [Fact]
    public void ContributeVariables_AwsAccount_SecretKeyMapped()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateAwsAccount());

        vars.ShouldContain(v => v.Name == "Squid.Account.SecretKey" && v.Value == "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
    }

    // === ContributeVariables — null/empty account ===

    [Fact]
    public void ContributeVariables_NullAccount_DefaultsToToken()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), null);

        vars.ShouldContain(v => v.Name == "Squid.Account.AccountType" && v.Value == "Token");
    }

    [Fact]
    public void ContributeVariables_NullAccount_CredentialFieldsEmpty()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), null);

        vars.ShouldContain(v => v.Name == "Squid.Account.Token" && v.Value == string.Empty);
        vars.ShouldContain(v => v.Name == "Squid.Account.Username" && v.Value == string.Empty);
        vars.ShouldContain(v => v.Name == "Squid.Account.Password" && v.Value == string.Empty);
        vars.ShouldContain(v => v.Name == "Squid.Account.AccessKey" && v.Value == string.Empty);
        vars.ShouldContain(v => v.Name == "Squid.Account.SecretKey" && v.Value == string.Empty);
        vars.ShouldContain(v => v.Name == "Squid.Account.ClientCertificateData" && v.Value == string.Empty);
        vars.ShouldContain(v => v.Name == "Squid.Account.ClientCertificateKeyData" && v.Value == string.Empty);
    }

    // === ContributeVariables — default endpoint values ===

    [Fact]
    public void ContributeVariables_NullNamespace_DefaultsToDefault()
    {
        var json = JsonSerializer.Serialize(new KubernetesEndpointDto
        {
            CommunicationStyle = "Kubernetes",
            ClusterUrl = "https://k8s:6443",
            Namespace = null
        });

        var vars = _contributor.ContributeVariables(json, CreateTokenAccount());

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.Namespace" && v.Value == "default");
    }

    [Fact]
    public void ContributeVariables_NullSkipTls_DefaultsToFalse()
    {
        var json = JsonSerializer.Serialize(new KubernetesEndpointDto
        {
            CommunicationStyle = "Kubernetes",
            ClusterUrl = "https://k8s:6443",
            SkipTlsVerification = null
        });

        var vars = _contributor.ContributeVariables(json, CreateTokenAccount());

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.SkipTlsVerification" && v.Value == "False");
    }

    // === ContributeVariables — static/fixed variables ===

    [Fact]
    public void ContributeVariables_SuppressEnvironmentLogging_AlwaysFalse()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateTokenAccount());

        vars.ShouldContain(v => v.Name == "Squid.Action.Script.SuppressEnvironmentLogging" && v.Value == "False");
    }

    [Fact]
    public void ContributeVariables_OutputKubectlVersion_AlwaysTrue()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateTokenAccount());

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.OutputKubectlVersion" && v.Value == "True");
    }

    [Fact]
    public void ContributeVariables_PrintEvaluatedVariables_AlwaysTrue()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateTokenAccount());

        vars.ShouldContain(v => v.Name == "SquidPrintEvaluatedVariables" && v.Value == "True");
    }

    // === ContributeVariables — bad input ===

    [Fact]
    public void ContributeVariables_InvalidJson_ReturnsEmpty()
    {
        var vars = _contributor.ContributeVariables("not-json", CreateTokenAccount());

        vars.ShouldBeEmpty();
    }

    [Fact]
    public void ContributeVariables_EmptyJson_ReturnsEmpty()
    {
        var vars = _contributor.ContributeVariables(string.Empty, CreateTokenAccount());

        vars.ShouldBeEmpty();
    }

    [Fact]
    public void ContributeVariables_NullJson_ReturnsEmpty()
    {
        var vars = _contributor.ContributeVariables(null, CreateTokenAccount());

        vars.ShouldBeEmpty();
    }
}
