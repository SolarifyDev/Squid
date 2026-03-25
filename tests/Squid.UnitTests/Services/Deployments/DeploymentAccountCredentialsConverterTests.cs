using System.Text.Json;
using Squid.Core.Services.Deployments.Account;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

namespace Squid.UnitTests.Services.Deployments;

public class DeploymentAccountCredentialsConverterTests
{
    // === Deserialize from string ===

    [Fact]
    public void Deserialize_AzureOidc_ReturnsCorrectType()
    {
        var json = JsonSerializer.Serialize(new AzureOidcCredentials
        {
            SubscriptionNumber = "sub-1",
            ClientId = "cid",
            TenantId = "tid",
            Jwt = "aud"
        });

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.AzureOidc, json);

        var creds = result.ShouldBeOfType<AzureOidcCredentials>();
        creds.SubscriptionNumber.ShouldBe("sub-1");
        creds.ClientId.ShouldBe("cid");
        creds.TenantId.ShouldBe("tid");
        creds.Jwt.ShouldBe("aud");
    }

    [Fact]
    public void Deserialize_GoogleCloudAccount_ReturnsCorrectType()
    {
        var json = JsonSerializer.Serialize(new GcpCredentials { JsonKey = "{\"type\":\"service_account\"}" });

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.GoogleCloudAccount, json);

        var creds = result.ShouldBeOfType<GcpCredentials>();
        creds.JsonKey.ShouldBe("{\"type\":\"service_account\"}");
    }

    [Fact]
    public void Deserialize_AmazonWebServicesOidcAccount_ReturnsCorrectType()
    {
        var json = JsonSerializer.Serialize(new AwsOidcCredentials { RoleArn = "arn:aws:iam::123:role/r", WebIdentityToken = "jwt-token" });

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.AmazonWebServicesOidcAccount, json);

        var creds = result.ShouldBeOfType<AwsOidcCredentials>();
        creds.RoleArn.ShouldBe("arn:aws:iam::123:role/r");
        creds.WebIdentityToken.ShouldBe("jwt-token");
    }

    // === Deserialize from JsonElement ===

    [Fact]
    public void DeserializeJsonElement_AzureServicePrincipal_ReturnsCorrectType()
    {
        var element = JsonSerializer.SerializeToElement(new AzureServicePrincipalCredentials
        {
            SubscriptionNumber = "sub-123",
            ClientId = "client-456",
            TenantId = "tenant-789",
            Key = "secret-key"
        });

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.AzureServicePrincipal, element);

        var creds = result.ShouldBeOfType<AzureServicePrincipalCredentials>();
        creds.SubscriptionNumber.ShouldBe("sub-123");
        creds.ClientId.ShouldBe("client-456");
        creds.TenantId.ShouldBe("tenant-789");
        creds.Key.ShouldBe("secret-key");
    }

    [Fact]
    public void DeserializeJsonElement_AzureOidc_ReturnsCorrectType()
    {
        var element = JsonSerializer.SerializeToElement(new AzureOidcCredentials
        {
            SubscriptionNumber = "sub-123",
            ClientId = "client-456",
            TenantId = "tenant-789",
            Jwt = "jwt-token"
        });

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.AzureOidc, element);

        var creds = result.ShouldBeOfType<AzureOidcCredentials>();
        creds.SubscriptionNumber.ShouldBe("sub-123");
        creds.ClientId.ShouldBe("client-456");
        creds.TenantId.ShouldBe("tenant-789");
        creds.Jwt.ShouldBe("jwt-token");
    }

    [Fact]
    public void DeserializeJsonElement_Token_ReturnsCorrectType()
    {
        var element = JsonSerializer.SerializeToElement(new TokenCredentials { Token = "my-token" });

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, element);

        var creds = result.ShouldBeOfType<TokenCredentials>();
        creds.Token.ShouldBe("my-token");
    }

    [Fact]
    public void DeserializeJsonElement_Null_ReturnsNull()
    {
        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, (JsonElement?)null);

        result.ShouldBeNull();
    }
}
