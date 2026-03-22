using System.Text.Json;
using Squid.Core.Services.Deployments.Account;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

namespace Squid.UnitTests.Services.Deployments;

public class DeploymentAccountCredentialsConverterTests
{
    [Fact]
    public void Deserialize_AzureOidc_ReturnsCorrectType()
    {
        var json = JsonSerializer.Serialize(new AzureOidcCredentials
        {
            SubscriptionNumber = "sub-1",
            ClientId = "cid",
            TenantId = "tid",
            Audience = "aud"
        });

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.AzureOidc, json);

        var creds = result.ShouldBeOfType<AzureOidcCredentials>();
        creds.SubscriptionNumber.ShouldBe("sub-1");
        creds.ClientId.ShouldBe("cid");
        creds.TenantId.ShouldBe("tid");
        creds.Audience.ShouldBe("aud");
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
}
