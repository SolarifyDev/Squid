using System.Text.Json;
using Squid.Core.Services.Deployments.Account;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

namespace Squid.UnitTests.Services.Deployments;

public class DeploymentAccountCredentialsConverterTests
{
    // ========================================================================
    // Serialize output format
    // ========================================================================

    [Fact]
    public void Serialize_Token_OutputsCamelCase()
    {
        var json = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = "my-token" });

        json.ShouldBe("""{"token":"my-token"}""");
    }

    [Fact]
    public void Serialize_UsernamePassword_OutputsCamelCase()
    {
        var json = DeploymentAccountCredentialsConverter.Serialize(new UsernamePasswordCredentials { Username = "admin", Password = "secret" });

        json.ShouldBe("""{"username":"admin","password":"secret"}""");
    }

    [Fact]
    public void Serialize_Null_ReturnsNull()
    {
        DeploymentAccountCredentialsConverter.Serialize(null).ShouldBeNull();
    }

    // ========================================================================
    // Deserialize from string — camelCase (new DB data)
    // ========================================================================

    [Fact]
    public void Deserialize_CamelCaseToken_ReturnsCorrectValue()
    {
        var json = """{"token":"my-token"}""";

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, json);

        var creds = result.ShouldBeOfType<TokenCredentials>();
        creds.Token.ShouldBe("my-token");
    }

    [Fact]
    public void Deserialize_CamelCaseUsernamePassword_ReturnsCorrectValue()
    {
        var json = """{"username":"admin","password":"secret"}""";

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.UsernamePassword, json);

        var creds = result.ShouldBeOfType<UsernamePasswordCredentials>();
        creds.Username.ShouldBe("admin");
        creds.Password.ShouldBe("secret");
    }

    // ========================================================================
    // Deserialize from string — PascalCase (existing DB data, backward compat)
    // ========================================================================

    [Fact]
    public void Deserialize_PascalCaseToken_ReturnsCorrectValue()
    {
        var json = """{"Token":"my-token"}""";

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, json);

        var creds = result.ShouldBeOfType<TokenCredentials>();
        creds.Token.ShouldBe("my-token");
    }

    [Fact]
    public void Deserialize_PascalCaseClientCertificate_ReturnsCorrectValue()
    {
        var json = """{"ClientCertificateData":"cert-data","ClientCertificateKeyData":"key-data"}""";

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.ClientCertificate, json);

        var creds = result.ShouldBeOfType<ClientCertificateCredentials>();
        creds.ClientCertificateData.ShouldBe("cert-data");
        creds.ClientCertificateKeyData.ShouldBe("key-data");
    }

    // ========================================================================
    // Deserialize from string — other types
    // ========================================================================

    [Fact]
    public void Deserialize_AzureOidc_ReturnsCorrectType()
    {
        var json = DeploymentAccountCredentialsConverter.Serialize(new AzureOidcCredentials
        {
            SubscriptionNumber = "sub-1", ClientId = "cid", TenantId = "tid", Jwt = "aud"
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
        var json = DeploymentAccountCredentialsConverter.Serialize(new GcpCredentials { JsonKey = "{\"type\":\"service_account\"}" });

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.GoogleCloudAccount, json);

        var creds = result.ShouldBeOfType<GcpCredentials>();
        creds.JsonKey.ShouldBe("{\"type\":\"service_account\"}");
    }

    [Fact]
    public void Deserialize_AmazonWebServicesOidcAccount_ReturnsCorrectType()
    {
        var json = DeploymentAccountCredentialsConverter.Serialize(new AwsOidcCredentials { RoleArn = "arn:aws:iam::123:role/r", WebIdentityToken = "jwt-token" });

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.AmazonWebServicesOidcAccount, json);

        var creds = result.ShouldBeOfType<AwsOidcCredentials>();
        creds.RoleArn.ShouldBe("arn:aws:iam::123:role/r");
        creds.WebIdentityToken.ShouldBe("jwt-token");
    }

    [Fact]
    public void Deserialize_NullString_ReturnsNull()
    {
        DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, (string)null).ShouldBeNull();
    }

    [Fact]
    public void Deserialize_EmptyString_ReturnsNull()
    {
        DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, string.Empty).ShouldBeNull();
    }

    // ========================================================================
    // Deserialize from JsonElement — camelCase (API input from frontend)
    // ========================================================================

    [Fact]
    public void DeserializeJsonElement_CamelCaseToken_ReturnsCorrectValue()
    {
        var element = JsonDocument.Parse("""{"token":"my-token"}""").RootElement;

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, element);

        var creds = result.ShouldBeOfType<TokenCredentials>();
        creds.Token.ShouldBe("my-token");
    }

    [Fact]
    public void DeserializeJsonElement_CamelCaseUsernamePassword_ReturnsCorrectValue()
    {
        var element = JsonDocument.Parse("""{"username":"admin","password":"secret"}""").RootElement;

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.UsernamePassword, element);

        var creds = result.ShouldBeOfType<UsernamePasswordCredentials>();
        creds.Username.ShouldBe("admin");
        creds.Password.ShouldBe("secret");
    }

    [Fact]
    public void DeserializeJsonElement_CamelCaseAws_ReturnsCorrectValue()
    {
        var element = JsonDocument.Parse("""{"accessKey":"AKIA","secretKey":"secret123"}""").RootElement;

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.AmazonWebServicesAccount, element);

        var creds = result.ShouldBeOfType<AwsCredentials>();
        creds.AccessKey.ShouldBe("AKIA");
        creds.SecretKey.ShouldBe("secret123");
    }

    // ========================================================================
    // Deserialize from JsonElement — PascalCase (backward compat)
    // ========================================================================

    [Fact]
    public void DeserializeJsonElement_PascalCaseToken_ReturnsCorrectValue()
    {
        var element = JsonDocument.Parse("""{"Token":"my-token"}""").RootElement;

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, element);

        var creds = result.ShouldBeOfType<TokenCredentials>();
        creds.Token.ShouldBe("my-token");
    }

    [Fact]
    public void DeserializeJsonElement_AzureServicePrincipal_ReturnsCorrectType()
    {
        var element = JsonSerializer.SerializeToElement(new AzureServicePrincipalCredentials
        {
            SubscriptionNumber = "sub-123", ClientId = "client-456", TenantId = "tenant-789", Key = "secret-key"
        });

        var result = DeploymentAccountCredentialsConverter.Deserialize(AccountType.AzureServicePrincipal, element);

        var creds = result.ShouldBeOfType<AzureServicePrincipalCredentials>();
        creds.SubscriptionNumber.ShouldBe("sub-123");
        creds.ClientId.ShouldBe("client-456");
        creds.TenantId.ShouldBe("tenant-789");
        creds.Key.ShouldBe("secret-key");
    }

    [Fact]
    public void DeserializeJsonElement_Null_ReturnsNull()
    {
        DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, (JsonElement?)null).ShouldBeNull();
    }

    // ========================================================================
    // Round-trip: Serialize → Deserialize
    // ========================================================================

    [Fact]
    public void RoundTrip_Token_PreservesValue()
    {
        var original = new TokenCredentials { Token = "eyJhbGciOi..." };

        var json = DeploymentAccountCredentialsConverter.Serialize(original);
        var restored = DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, json);

        var creds = restored.ShouldBeOfType<TokenCredentials>();
        creds.Token.ShouldBe(original.Token);
    }

    [Fact]
    public void RoundTrip_UsernamePassword_PreservesValues()
    {
        var original = new UsernamePasswordCredentials { Username = "admin", Password = "P@ss" };

        var json = DeploymentAccountCredentialsConverter.Serialize(original);
        var restored = DeploymentAccountCredentialsConverter.Deserialize(AccountType.UsernamePassword, json);

        var creds = restored.ShouldBeOfType<UsernamePasswordCredentials>();
        creds.Username.ShouldBe("admin");
        creds.Password.ShouldBe("P@ss");
    }

    [Fact]
    public void RoundTrip_AwsRole_PreservesAllFields()
    {
        var original = new AwsRoleCredentials { AccessKey = "AK", SecretKey = "SK", RoleArn = "arn", SessionDuration = "3600", ExternalId = "ext" };

        var json = DeploymentAccountCredentialsConverter.Serialize(original);
        var restored = DeploymentAccountCredentialsConverter.Deserialize(AccountType.AmazonWebServicesRoleAccount, json);

        var creds = restored.ShouldBeOfType<AwsRoleCredentials>();
        creds.AccessKey.ShouldBe("AK");
        creds.SecretKey.ShouldBe("SK");
        creds.RoleArn.ShouldBe("arn");
        creds.SessionDuration.ShouldBe("3600");
        creds.ExternalId.ShouldBe("ext");
    }

    [Fact]
    public void RoundTrip_SshKeyPair_PreservesAllFields()
    {
        var original = new SshKeyPairCredentials { Username = "deploy", PrivateKeyFile = "key-data", PrivateKeyPassphrase = "pass" };

        var json = DeploymentAccountCredentialsConverter.Serialize(original);
        var restored = DeploymentAccountCredentialsConverter.Deserialize(AccountType.SshKeyPair, json);

        var creds = restored.ShouldBeOfType<SshKeyPairCredentials>();
        creds.Username.ShouldBe("deploy");
        creds.PrivateKeyFile.ShouldBe("key-data");
        creds.PrivateKeyPassphrase.ShouldBe("pass");
    }

    [Fact]
    public void RoundTrip_OpenClawGateway_PreservesBothTokens()
    {
        var original = new OpenClawGatewayCredentials { GatewayToken = "gw-token", HooksToken = "hooks-token" };

        var json = DeploymentAccountCredentialsConverter.Serialize(original);
        var restored = DeploymentAccountCredentialsConverter.Deserialize(AccountType.OpenClawGateway, json);

        var creds = restored.ShouldBeOfType<OpenClawGatewayCredentials>();
        creds.GatewayToken.ShouldBe("gw-token");
        creds.HooksToken.ShouldBe("hooks-token");
    }

    [Fact]
    public void Serialize_OpenClawGateway_OutputsCamelCase()
    {
        var json = DeploymentAccountCredentialsConverter.Serialize(new OpenClawGatewayCredentials { GatewayToken = "gw", HooksToken = "hk" });

        json.ShouldBe("""{"gatewayToken":"gw","hooksToken":"hk"}""");
    }
}
