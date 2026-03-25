using System.Text.Json;
using Squid.Core.Services.Deployments.Account;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

namespace Squid.UnitTests.Services.Deployments.Account;

public class DeploymentAccountServiceTests
{
    // === BuildCredentialsSummary — Azure ===

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildCredentialsSummary_AzureServicePrincipal_ReturnsSummary(bool hasKey)
    {
        var creds = new AzureServicePrincipalCredentials
        {
            SubscriptionNumber = "sub-1",
            ClientId = "cid-1",
            TenantId = "tid-1",
            Key = hasKey ? "secret" : null
        };

        var result = DeploymentAccountService.BuildCredentialsSummary(AccountType.AzureServicePrincipal, creds);

        var summary = result.ShouldBeOfType<AzureServicePrincipalCredentialsSummary>();
        summary.SubscriptionNumber.ShouldBe("sub-1");
        summary.ClientId.ShouldBe("cid-1");
        summary.TenantId.ShouldBe("tid-1");
        summary.KeyHasValue.ShouldBe(hasKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildCredentialsSummary_AzureOidc_ReturnsSummary(bool hasJwt)
    {
        var creds = new AzureOidcCredentials
        {
            SubscriptionNumber = "sub-1",
            ClientId = "cid-1",
            TenantId = "tid-1",
            Jwt = hasJwt ? "token" : null
        };

        var result = DeploymentAccountService.BuildCredentialsSummary(AccountType.AzureOidc, creds);

        var summary = result.ShouldBeOfType<AzureOidcCredentialsSummary>();
        summary.SubscriptionNumber.ShouldBe("sub-1");
        summary.ClientId.ShouldBe("cid-1");
        summary.TenantId.ShouldBe("tid-1");
        summary.JwtHasValue.ShouldBe(hasJwt);
    }

    // === SerializeEnvironmentIds ===

    [Fact]
    public void SerializeEnvironmentIds_WithValues_SerializesCorrectly()
    {
        var result = DeploymentAccountService.SerializeEnvironmentIds(new List<int> { 1, 3, 5 });

        result.ShouldBe("[1,3,5]");
    }

    [Fact]
    public void SerializeEnvironmentIds_EmptyList_ReturnsNull()
    {
        var result = DeploymentAccountService.SerializeEnvironmentIds(new List<int>());

        result.ShouldBeNull();
    }

    [Fact]
    public void SerializeEnvironmentIds_Null_ReturnsNull()
    {
        var result = DeploymentAccountService.SerializeEnvironmentIds(null);

        result.ShouldBeNull();
    }

    // === DeserializeEnvironmentIds ===

    [Fact]
    public void DeserializeEnvironmentIds_ValidJson_ReturnsIds()
    {
        var result = DeploymentAccountService.DeserializeEnvironmentIds("[1,3,5]");

        result.ShouldBe(new List<int> { 1, 3, 5 });
    }

    [Fact]
    public void DeserializeEnvironmentIds_EmptyString_ReturnsEmptyList()
    {
        var result = DeploymentAccountService.DeserializeEnvironmentIds(string.Empty);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void DeserializeEnvironmentIds_Null_ReturnsEmptyList()
    {
        var result = DeploymentAccountService.DeserializeEnvironmentIds(null);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void DeserializeEnvironmentIds_MalformedJson_ReturnsEmptyList()
    {
        var result = DeploymentAccountService.DeserializeEnvironmentIds("not-json");

        result.ShouldBeEmpty();
    }
}
