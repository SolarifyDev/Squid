using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class EndpointContextBuilderTests
{
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
        var cert = new Certificate { Id = 5, CertificateData = "cert-data-base64", HasPrivateKey = true };

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

        result.GetCertificate(EndpointResourceType.ClientCertificate).ShouldBe("cert-data-base64");
        var accountData = result.GetAccountData();
        accountData.ShouldNotBeNull();
        accountData.AuthenticationAccountType.ShouldBe(AccountType.ClientCertificate);
    }

    [Fact]
    public async Task Build_WithClusterCertificate_SetsCertificateData()
    {
        var endpointJson = """{"communicationStyle":"KubernetesApi"}""";
        var cert = new Certificate { Id = 7, CertificateData = "cluster-ca-data", HasPrivateKey = false };

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

        result.GetCertificate(EndpointResourceType.ClusterCertificate).ShouldBe("cluster-ca-data");
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
        var cert = new Certificate { Id = 7, CertificateData = "ca-data", HasPrivateKey = false };

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
        result.GetCertificate(EndpointResourceType.ClusterCertificate).ShouldBe("ca-data");
    }

    // ========================================================================
    // EnrichCredentialsWithClientCertificate — static method tests
    // ========================================================================

    [Fact]
    public void EnrichCredentials_NoExistingAccount_CreatesClientCertificateAccount()
    {
        var ctx = new EndpointContext { EndpointJson = "{}" };
        var cert = new Certificate { CertificateData = "cert-pem", HasPrivateKey = true };

        EndpointContextBuilder.EnrichCredentialsWithClientCertificate(ctx, cert);

        var accountData = ctx.GetAccountData();
        accountData.ShouldNotBeNull();
        accountData.AuthenticationAccountType.ShouldBe(AccountType.ClientCertificate);

        var creds = DeploymentAccountCredentialsConverter.Deserialize(AccountType.ClientCertificate, accountData.CredentialsJson) as ClientCertificateCredentials;
        creds.ShouldNotBeNull();
        creds.ClientCertificateData.ShouldBe("cert-pem");
        creds.ClientCertificateKeyData.ShouldBe("cert-pem");
    }

    [Fact]
    public void EnrichCredentials_NoPrivateKey_OnlySetsCertificateData()
    {
        var ctx = new EndpointContext { EndpointJson = "{}" };
        var cert = new Certificate { CertificateData = "cert-pem", HasPrivateKey = false };

        EndpointContextBuilder.EnrichCredentialsWithClientCertificate(ctx, cert);

        var accountData = ctx.GetAccountData();
        var creds = DeploymentAccountCredentialsConverter.Deserialize(AccountType.ClientCertificate, accountData.CredentialsJson) as ClientCertificateCredentials;
        creds.ShouldNotBeNull();
        creds.ClientCertificateData.ShouldBe("cert-pem");
        creds.ClientCertificateKeyData.ShouldBeNull();
    }
}
