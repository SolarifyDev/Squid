using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using Squid.UnitTests.Services.Machines;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class EndpointContextBuilderTests
{
    private const string TestCertPem = "-----BEGIN CERTIFICATE-----\nTESTDATA\n-----END CERTIFICATE-----";
    private const string TestCertWithKeyPem = "-----BEGIN CERTIFICATE-----\nTESTDATA\n-----END CERTIFICATE-----\n-----BEGIN PRIVATE KEY-----\nKEYDATA\n-----END PRIVATE KEY-----";
    private static string B64(string pem) => Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));
    private readonly Mock<IDeploymentAccountDataProvider> _accountDataProvider = new();
    private readonly Mock<ICertificateDataProvider> _certificateDataProvider = new();
    private readonly Mock<IEndpointVariableContributor> _variableContributor = new();
    private readonly EndpointContextBuilder _builder;

    public EndpointContextBuilderTests()
    {
        _builder = new EndpointContextBuilder(_accountDataProvider.Object, _certificateDataProvider.Object);
    }

    [Fact]
    public async Task Build_WithAuthAccount_SetsAccountData()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";
        var account = new DeploymentAccount { Id = 10, AccountType = AccountType.Token, Credentials = """{"token":"test-token"}""" };

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 10 }
                }
            });

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(account);

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        result.EndpointJson.ShouldBe(endpointJson);
        var accountData = result.GetAccountData();
        accountData.ShouldNotBeNull();
        accountData.AuthenticationAccountType.ShouldBe(AccountType.Token);
        accountData.CredentialsJson.ShouldContain("test-token");
    }

    [Fact]
    public async Task Build_NoAuthAccount_ReturnsContextWithoutCredentials()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences { References = new List<EndpointResourceReference>() });

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        result.EndpointJson.ShouldBe(endpointJson);
        result.GetAccountData().ShouldBeNull();
        _accountDataProvider.Verify(a => a.GetAccountByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Build_AccountNotFound_ReturnsContextWithoutCredentials()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 99 }
                }
            });

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((DeploymentAccount)null);

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        result.GetAccountData().ShouldBeNull();
    }

    [Fact]
    public async Task Build_WithClientCertificate_EnrichesCredentials()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";
        var cert = new Certificate { Id = 5, CertificateData = B64(TestCertWithKeyPem), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = true };

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.ClientCertificate, ResourceId = 5 }
                }
            });

        _certificateDataProvider.Setup(c => c.GetCertificateByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(cert);

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        result.GetCertificate(EndpointResourceType.ClientCertificate).ShouldBe(TestCertWithKeyPem);
        var accountData = result.GetAccountData();
        accountData.ShouldNotBeNull();
        accountData.AuthenticationAccountType.ShouldBe(AccountType.ClientCertificate);
    }

    [Fact]
    public async Task Build_WithClusterCertificate_SetsCertificateData()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";
        var cert = new Certificate { Id = 7, CertificateData = B64(TestCertPem), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = false };

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.ClusterCertificate, ResourceId = 7 }
                }
            });

        _certificateDataProvider.Setup(c => c.GetCertificateByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(cert);

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        result.GetCertificate(EndpointResourceType.ClusterCertificate).ShouldBe(TestCertPem);
        result.GetAccountData().ShouldBeNull();
    }

    [Fact]
    public async Task Build_CertificateNotFound_SkipsGracefully()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.ClientCertificate, ResourceId = 99 }
                }
            });

        _certificateDataProvider.Setup(c => c.GetCertificateByIdAsync(99, It.IsAny<CancellationToken>())).ReturnsAsync((Certificate)null);

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        result.GetCertificate(EndpointResourceType.ClientCertificate).ShouldBeNull();
    }

    [Fact]
    public async Task Build_WithAccountAndCertificate_ResolvesBoth()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";
        var account = new DeploymentAccount { Id = 10, AccountType = AccountType.Token, Credentials = """{"token":"my-token"}""" };
        var cert = new Certificate { Id = 7, CertificateData = B64(TestCertPem), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = false };

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 10 },
                    new() { Type = EndpointResourceType.ClusterCertificate, ResourceId = 7 }
                }
            });

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        _certificateDataProvider.Setup(c => c.GetCertificateByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(cert);

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        result.GetAccountData().ShouldNotBeNull();
        result.GetAccountData().AuthenticationAccountType.ShouldBe(AccountType.Token);
        result.GetCertificate(EndpointResourceType.ClusterCertificate).ShouldBe(TestCertPem);
    }

    // ========================================================================
    // EnrichCredentialsWithClientCertificate — static method tests
    // ========================================================================

    [Fact]
    public void EnrichCredentials_NoExistingAccount_CreatesClientCertificateAccount()
    {
        var ctx = new EndpointContext { EndpointJson = "{}" };
        var cert = new Certificate { CertificateData = B64(TestCertWithKeyPem), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = true };

        EndpointContextBuilder.EnrichCredentialsWithClientCertificate(ctx, cert);

        var accountData = ctx.GetAccountData();
        accountData.ShouldNotBeNull();
        accountData.AuthenticationAccountType.ShouldBe(AccountType.ClientCertificate);

        var creds = DeploymentAccountCredentialsConverter.Deserialize(AccountType.ClientCertificate, accountData.CredentialsJson) as ClientCertificateCredentials;
        creds.ShouldNotBeNull();
        creds.ClientCertificateData.ShouldBe(TestCertWithKeyPem);
        creds.ClientCertificateKeyData.ShouldBe(TestCertWithKeyPem);
    }

    [Fact]
    public void EnrichCredentials_NoPrivateKey_OnlySetsCertificateData()
    {
        var ctx = new EndpointContext { EndpointJson = "{}" };
        var cert = new Certificate { CertificateData = B64(TestCertPem), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = false };

        EndpointContextBuilder.EnrichCredentialsWithClientCertificate(ctx, cert);

        var accountData = ctx.GetAccountData();
        var creds = DeploymentAccountCredentialsConverter.Deserialize(AccountType.ClientCertificate, accountData.CredentialsJson) as ClientCertificateCredentials;
        creds.ShouldNotBeNull();
        creds.ClientCertificateData.ShouldBe(TestCertPem);
        creds.ClientCertificateKeyData.ShouldBeNull();
    }

    // ========================================================================
    // Warning Log Verification (Task 1 — silent failure logging)
    // ========================================================================

    [Fact]
    public async Task Build_AccountReferenced_NotFoundInDb_ReturnsWithoutCredentials()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 42 }
                }
            });

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync((DeploymentAccount)null);

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        result.GetAccountData().ShouldBeNull();
        _accountDataProvider.Verify(a => a.GetAccountByIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Build_CertificateReferenced_NotFoundInDb_ReturnsWithoutCertificate()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.ClientCertificate, ResourceId = 55 }
                }
            });

        _certificateDataProvider.Setup(c => c.GetCertificateByIdAsync(55, It.IsAny<CancellationToken>())).ReturnsAsync((Certificate)null);

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        result.GetCertificate(EndpointResourceType.ClientCertificate).ShouldBeNull();
        _certificateDataProvider.Verify(c => c.GetCertificateByIdAsync(55, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========================================================================
    // All Credential Types Through Builder
    // ========================================================================

    [Fact]
    public async Task Build_UsernamePasswordAccount_SetsCorrectAccountData()
    {
        var creds = DeploymentAccountCredentialsConverter.Serialize(new UsernamePasswordCredentials { Username = "admin", Password = "secret" });
        var result = await BuildWithAccount(AccountType.UsernamePassword, creds);

        result.GetAccountData().AuthenticationAccountType.ShouldBe(AccountType.UsernamePassword);
        result.GetAccountData().CredentialsJson.ShouldContain("admin");
    }

    [Fact]
    public async Task Build_AwsAccount_SetsCorrectAccountData()
    {
        var creds = DeploymentAccountCredentialsConverter.Serialize(new AwsCredentials { AccessKey = "AKID", SecretKey = "SKEY" });
        var result = await BuildWithAccount(AccountType.AmazonWebServicesAccount, creds);

        result.GetAccountData().AuthenticationAccountType.ShouldBe(AccountType.AmazonWebServicesAccount);
        result.GetAccountData().CredentialsJson.ShouldContain("AKID");
    }

    [Fact]
    public async Task Build_AzureServicePrincipalAccount_SetsCorrectAccountData()
    {
        var creds = DeploymentAccountCredentialsConverter.Serialize(new AzureServicePrincipalCredentials { ClientId = "cid", TenantId = "tid", Key = "k" });
        var result = await BuildWithAccount(AccountType.AzureServicePrincipal, creds);

        result.GetAccountData().AuthenticationAccountType.ShouldBe(AccountType.AzureServicePrincipal);
        result.GetAccountData().CredentialsJson.ShouldContain("cid");
    }

    [Fact]
    public async Task Build_GcpAccount_SetsCorrectAccountData()
    {
        var creds = DeploymentAccountCredentialsConverter.Serialize(new GcpCredentials { JsonKey = "gcp-json-key" });
        var result = await BuildWithAccount(AccountType.GoogleCloudAccount, creds);

        result.GetAccountData().AuthenticationAccountType.ShouldBe(AccountType.GoogleCloudAccount);
        result.GetAccountData().CredentialsJson.ShouldContain("gcp-json-key");
    }

    // ========================================================================
    // Client Certificate Enrichment Edge Cases
    // ========================================================================

    [Fact]
    public void EnrichCredentials_ClientCertWithExistingTokenAccount_KeepsTokenAccountType()
    {
        var ctx = new EndpointContext { EndpointJson = "{}" };
        ctx.SetAccountData(AccountType.Token, """{"token":"my-token"}""");

        var cert = new Certificate { CertificateData = B64(TestCertWithKeyPem), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = true };

        EndpointContextBuilder.EnrichCredentialsWithClientCertificate(ctx, cert);

        var accountData = ctx.GetAccountData();
        accountData.AuthenticationAccountType.ShouldBe(AccountType.Token);
    }

    [Fact]
    public void EnrichCredentials_ClientCertWithExistingClientCertAccount_MergesCredentials()
    {
        var ctx = new EndpointContext { EndpointJson = "{}" };
        var existingCreds = DeploymentAccountCredentialsConverter.Serialize(new ClientCertificateCredentials { ClientCertificateData = "old-cert", ClientCertificateKeyData = "old-key" });
        ctx.SetAccountData(AccountType.ClientCertificate, existingCreds);

        var cert = new Certificate { CertificateData = B64(TestCertWithKeyPem), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = true };

        EndpointContextBuilder.EnrichCredentialsWithClientCertificate(ctx, cert);

        var accountData = ctx.GetAccountData();
        accountData.AuthenticationAccountType.ShouldBe(AccountType.ClientCertificate);

        var creds = DeploymentAccountCredentialsConverter.Deserialize(AccountType.ClientCertificate, accountData.CredentialsJson) as ClientCertificateCredentials;
        creds.ShouldNotBeNull();
        creds.ClientCertificateData.ShouldBe(TestCertWithKeyPem);
        creds.ClientCertificateKeyData.ShouldBe(TestCertWithKeyPem);
    }

    // ========================================================================
    // Multiple Resource References
    // ========================================================================

    [Fact]
    public async Task Build_AccountAndBothCertTypes_ResolvesAll()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";
        var account = new DeploymentAccount { Id = 10, AccountType = AccountType.Token, Credentials = """{"token":"tok"}""" };
        var clientCert = new Certificate { Id = 5, CertificateData = B64(TestCertWithKeyPem), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = true };
        var clusterCert = new Certificate { Id = 7, CertificateData = B64(TestCertPem), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = false };

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 10 },
                    new() { Type = EndpointResourceType.ClientCertificate, ResourceId = 5 },
                    new() { Type = EndpointResourceType.ClusterCertificate, ResourceId = 7 }
                }
            });

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(account);
        _certificateDataProvider.Setup(c => c.GetCertificateByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(clientCert);
        _certificateDataProvider.Setup(c => c.GetCertificateByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(clusterCert);

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        result.GetAccountData().ShouldNotBeNull();
        result.GetCertificate(EndpointResourceType.ClientCertificate).ShouldBe(TestCertWithKeyPem);
        result.GetCertificate(EndpointResourceType.ClusterCertificate).ShouldBe(TestCertPem);
    }

    [Fact]
    public async Task Build_MultipleAccountReferences_UsesFirst()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";
        var account = new DeploymentAccount { Id = 10, AccountType = AccountType.Token, Credentials = """{"token":"tok"}""" };

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 10 },
                    new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 20 }
                }
            });

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(account);

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        result.GetAccountData().ShouldNotBeNull();
        _accountDataProvider.Verify(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>()), Times.Once);
        _accountDataProvider.Verify(a => a.GetAccountByIdAsync(20, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ========================================================================
    // DecodeCertificatePem — Format-Specific Tests
    // ========================================================================

    [Fact]
    public void DecodeCertificatePem_PemFormat_ReturnsDecodedPemText()
    {
        var cert = new Certificate { CertificateData = B64(TestCertPem), CertificateDataFormat = CertificateDataFormat.Pem };

        var result = EndpointContextBuilder.DecodeCertificatePem(cert);

        result.ShouldBe(TestCertPem);
    }

    [Fact]
    public void DecodeCertificatePem_DerFormat_ConvertsToPem()
    {
        var (derBytes, _) = TestCertHelper.GenerateSelfSignedDer();
        var cert = new Certificate { CertificateData = Convert.ToBase64String(derBytes), CertificateDataFormat = CertificateDataFormat.Der };

        var result = EndpointContextBuilder.DecodeCertificatePem(cert);

        result.ShouldStartWith("-----BEGIN CERTIFICATE-----");
        result.ShouldEndWith("-----END CERTIFICATE-----");
    }

    [Fact]
    public void DecodeCertificatePem_PfxFormat_ConvertsToPem()
    {
        var (pfxBytes, password) = TestCertHelper.GenerateSelfSignedPfx();
        var cert = new Certificate { CertificateData = Convert.ToBase64String(pfxBytes), CertificateDataFormat = CertificateDataFormat.Pkcs12, Password = password };

        var result = EndpointContextBuilder.DecodeCertificatePem(cert);

        result.ShouldStartWith("-----BEGIN CERTIFICATE-----");
        result.ShouldEndWith("-----END CERTIFICATE-----");
    }

    [Fact]
    public void DecodeCertificatePem_EmptyData_ReturnsEmpty()
    {
        var cert = new Certificate { CertificateData = "", CertificateDataFormat = CertificateDataFormat.Pem };

        var result = EndpointContextBuilder.DecodeCertificatePem(cert);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void DecodeCertificatePem_NullData_ReturnsEmpty()
    {
        var cert = new Certificate { CertificateData = null, CertificateDataFormat = CertificateDataFormat.Pem };

        var result = EndpointContextBuilder.DecodeCertificatePem(cert);

        result.ShouldBeEmpty();
    }

    // ========================================================================
    // DecodePrivateKeyPem — Format-Specific Tests
    // ========================================================================

    [Fact]
    public void DecodePrivateKeyPem_PemFormat_ReturnsFullContent()
    {
        var cert = new Certificate { CertificateData = B64(TestCertWithKeyPem), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = true };

        var result = EndpointContextBuilder.DecodePrivateKeyPem(cert);

        result.ShouldBe(TestCertWithKeyPem);
    }

    [Fact]
    public void DecodePrivateKeyPem_PfxFormat_ExtractsPrivateKey()
    {
        var (pfxBytes, password) = TestCertHelper.GenerateSelfSignedPfx();
        var cert = new Certificate { CertificateData = Convert.ToBase64String(pfxBytes), CertificateDataFormat = CertificateDataFormat.Pkcs12, Password = password, HasPrivateKey = true };

        var result = EndpointContextBuilder.DecodePrivateKeyPem(cert);

        result.ShouldStartWith("-----BEGIN PRIVATE KEY-----");
        result.ShouldEndWith("-----END PRIVATE KEY-----");
    }

    [Fact]
    public void DecodePrivateKeyPem_DerFormat_ReturnsEmpty()
    {
        var (derBytes, _) = TestCertHelper.GenerateSelfSignedDer();
        var cert = new Certificate { CertificateData = Convert.ToBase64String(derBytes), CertificateDataFormat = CertificateDataFormat.Der, HasPrivateKey = true };

        var result = EndpointContextBuilder.DecodePrivateKeyPem(cert);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void DecodePrivateKeyPem_NoPrivateKey_ReturnsEmpty()
    {
        var cert = new Certificate { CertificateData = B64(TestCertPem), CertificateDataFormat = CertificateDataFormat.Pem, HasPrivateKey = false };

        var result = EndpointContextBuilder.DecodePrivateKeyPem(cert);

        result.ShouldBeEmpty();
    }

    // ========================================================================
    // Full Builder with Real Certificates (DER/PFX formats)
    // ========================================================================

    [Theory]
    [InlineData(CertificateDataFormat.Der)]
    [InlineData(CertificateDataFormat.Pkcs12)]
    public async Task Build_WithClusterCertificate_NonPemFormat_DecodesToPem(CertificateDataFormat format)
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";
        var (certBytes, password) = format == CertificateDataFormat.Der
            ? TestCertHelper.GenerateSelfSignedDer()
            : TestCertHelper.GenerateSelfSignedPfx();
        var cert = new Certificate { Id = 7, CertificateData = Convert.ToBase64String(certBytes), CertificateDataFormat = format, Password = password, HasPrivateKey = false };

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.ClusterCertificate, ResourceId = 7 }
                }
            });

        _certificateDataProvider.Setup(c => c.GetCertificateByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(cert);

        var result = await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);

        var decoded = result.GetCertificate(EndpointResourceType.ClusterCertificate);
        decoded.ShouldStartWith("-----BEGIN CERTIFICATE-----");
        decoded.ShouldEndWith("-----END CERTIFICATE-----");
    }

    // ========================================================================
    // Helper
    // ========================================================================

    private async Task<EndpointContext> BuildWithAccount(AccountType accountType, string credentials)
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";
        var account = new DeploymentAccount { Id = 10, AccountType = accountType, Credentials = credentials };

        _variableContributor.Setup(v => v.ParseResourceReferences(endpointJson))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                {
                    new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 10 }
                }
            });

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(account);

        return await _builder.BuildAsync(endpointJson, _variableContributor.Object, CancellationToken.None);
    }
}
