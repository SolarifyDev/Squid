using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Message.Commands.Deployments.Account;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;

namespace Squid.UnitTests.Services.Deployments.Account;

public class DeploymentAccountServiceTests
{
    private readonly Mock<IDeploymentAccountDataProvider> _dataProvider = new();
    private readonly DeploymentAccountService _sut;

    public DeploymentAccountServiceTests()
    {
        _sut = new DeploymentAccountService(_dataProvider.Object);
    }

    // ========================================================================
    // CreateAsync
    // ========================================================================

    [Fact]
    public async Task Create_TokenWithCamelCaseInput_SavesCredentials()
    {
        var command = new CreateDeploymentAccountCommand
        {
            SpaceId = 1,
            Name = "K8s Token",
            AccountType = AccountType.Token,
            Credentials = JsonDocument.Parse("""{"token":"eyJhbGciOi..."}""").RootElement
        };

        DeploymentAccount saved = null;
        _dataProvider.Setup(p => p.AddAccountAsync(It.IsAny<DeploymentAccount>(), true, It.IsAny<CancellationToken>()))
            .Callback<DeploymentAccount, bool, CancellationToken>((a, _, _) => saved = a)
            .ReturnsAsync((DeploymentAccount a, bool _, CancellationToken _) => a);

        var result = await _sut.CreateAsync(command, CancellationToken.None);

        saved.ShouldNotBeNull();
        saved.Credentials.ShouldNotBeNullOrEmpty();

        var restored = DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, saved.Credentials);
        var creds = restored.ShouldBeOfType<TokenCredentials>();
        creds.Token.ShouldBe("eyJhbGciOi...");

        var summary = result.DeploymentAccount.Credentials.ShouldBeOfType<TokenCredentialsSummary>();
        summary.TokenHasValue.ShouldBeTrue();
    }

    [Fact]
    public async Task Create_UsernamePasswordWithCamelCaseInput_SavesCredentials()
    {
        var command = new CreateDeploymentAccountCommand
        {
            SpaceId = 1,
            Name = "SSH Account",
            AccountType = AccountType.UsernamePassword,
            Credentials = JsonDocument.Parse("""{"username":"admin","password":"secret"}""").RootElement
        };

        DeploymentAccount saved = null;
        _dataProvider.Setup(p => p.AddAccountAsync(It.IsAny<DeploymentAccount>(), true, It.IsAny<CancellationToken>()))
            .Callback<DeploymentAccount, bool, CancellationToken>((a, _, _) => saved = a)
            .ReturnsAsync((DeploymentAccount a, bool _, CancellationToken _) => a);

        var result = await _sut.CreateAsync(command, CancellationToken.None);

        var restored = DeploymentAccountCredentialsConverter.Deserialize(AccountType.UsernamePassword, saved.Credentials);
        var creds = restored.ShouldBeOfType<UsernamePasswordCredentials>();
        creds.Username.ShouldBe("admin");
        creds.Password.ShouldBe("secret");

        var summary = result.DeploymentAccount.Credentials.ShouldBeOfType<UsernamePasswordCredentialsSummary>();
        summary.Username.ShouldBe("admin");
        summary.PasswordHasValue.ShouldBeTrue();
    }

    [Fact]
    public async Task Create_NullCredentials_SavesNullCredentials()
    {
        var command = new CreateDeploymentAccountCommand
        {
            SpaceId = 1,
            Name = "Empty Account",
            AccountType = AccountType.Token,
            Credentials = null
        };

        DeploymentAccount saved = null;
        _dataProvider.Setup(p => p.AddAccountAsync(It.IsAny<DeploymentAccount>(), true, It.IsAny<CancellationToken>()))
            .Callback<DeploymentAccount, bool, CancellationToken>((a, _, _) => saved = a)
            .ReturnsAsync((DeploymentAccount a, bool _, CancellationToken _) => a);

        var result = await _sut.CreateAsync(command, CancellationToken.None);

        saved.Credentials.ShouldBeNull();

        var summary = result.DeploymentAccount.Credentials.ShouldBeOfType<TokenCredentialsSummary>();
        summary.TokenHasValue.ShouldBeFalse();
    }

    // ========================================================================
    // UpdateAsync
    // ========================================================================

    [Fact]
    public async Task Update_WithNewCredentials_ReplacesExisting()
    {
        var existing = new DeploymentAccount
        {
            Id = 1, SpaceId = 1, Name = "Old", AccountType = AccountType.Token,
            Credentials = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = "old-token" })
        };

        _dataProvider.Setup(p => p.GetAccountsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentAccount> { existing });

        var command = new UpdateDeploymentAccountCommand
        {
            Id = 1, SpaceId = 1, Name = "Updated", AccountType = AccountType.Token,
            Credentials = JsonDocument.Parse("""{"token":"new-token"}""").RootElement
        };

        await _sut.UpdateAsync(command, CancellationToken.None);

        var restored = DeploymentAccountCredentialsConverter.Deserialize(AccountType.Token, existing.Credentials);
        var creds = restored.ShouldBeOfType<TokenCredentials>();
        creds.Token.ShouldBe("new-token");
    }

    [Fact]
    public async Task Update_NullCredentials_PreservesExisting()
    {
        var originalJson = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = "keep-me" });
        var existing = new DeploymentAccount
        {
            Id = 1, SpaceId = 1, Name = "Old", AccountType = AccountType.Token,
            Credentials = originalJson
        };

        _dataProvider.Setup(p => p.GetAccountsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentAccount> { existing });

        var command = new UpdateDeploymentAccountCommand
        {
            Id = 1, SpaceId = 1, Name = "Renamed", AccountType = AccountType.Token,
            Credentials = null
        };

        await _sut.UpdateAsync(command, CancellationToken.None);

        existing.Credentials.ShouldBe(originalJson);
        existing.Name.ShouldBe("Renamed");
    }

    [Fact]
    public async Task Update_NotFound_Throws()
    {
        _dataProvider.Setup(p => p.GetAccountsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentAccount>());

        var command = new UpdateDeploymentAccountCommand { Id = 99, AccountType = AccountType.Token };

        await Should.ThrowAsync<Exception>(() => _sut.UpdateAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task Update_EnvironmentIds_UpdatesWhenProvided()
    {
        var existing = new DeploymentAccount
        {
            Id = 1, SpaceId = 1, Name = "Acc", AccountType = AccountType.Token,
            Credentials = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = "t" }),
            EnvironmentIds = "[1,2]"
        };

        _dataProvider.Setup(p => p.GetAccountsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentAccount> { existing });

        var command = new UpdateDeploymentAccountCommand
        {
            Id = 1, SpaceId = 1, Name = "Acc", AccountType = AccountType.Token,
            EnvironmentIds = new List<int> { 3, 4 }
        };

        await _sut.UpdateAsync(command, CancellationToken.None);

        existing.EnvironmentIds.ShouldBe("[3,4]");
    }

    [Fact]
    public async Task Update_NullEnvironmentIds_PreservesExisting()
    {
        var existing = new DeploymentAccount
        {
            Id = 1, SpaceId = 1, Name = "Acc", AccountType = AccountType.Token,
            Credentials = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = "t" }),
            EnvironmentIds = "[1,2]"
        };

        _dataProvider.Setup(p => p.GetAccountsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentAccount> { existing });

        var command = new UpdateDeploymentAccountCommand
        {
            Id = 1, SpaceId = 1, Name = "Acc", AccountType = AccountType.Token,
            EnvironmentIds = null
        };

        await _sut.UpdateAsync(command, CancellationToken.None);

        existing.EnvironmentIds.ShouldBe("[1,2]");
    }

    // ========================================================================
    // BuildCredentialsSummary
    // ========================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildCredentialsSummary_Token_ReturnsSummary(bool hasToken)
    {
        var creds = new TokenCredentials { Token = hasToken ? "my-token" : null };

        var result = DeploymentAccountService.BuildCredentialsSummary(AccountType.Token, creds);

        var summary = result.ShouldBeOfType<TokenCredentialsSummary>();
        summary.TokenHasValue.ShouldBe(hasToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildCredentialsSummary_AzureServicePrincipal_ReturnsSummary(bool hasKey)
    {
        var creds = new AzureServicePrincipalCredentials
        {
            SubscriptionNumber = "sub-1", ClientId = "cid-1", TenantId = "tid-1",
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
            SubscriptionNumber = "sub-1", ClientId = "cid-1", TenantId = "tid-1",
            Jwt = hasJwt ? "token" : null
        };

        var result = DeploymentAccountService.BuildCredentialsSummary(AccountType.AzureOidc, creds);

        var summary = result.ShouldBeOfType<AzureOidcCredentialsSummary>();
        summary.SubscriptionNumber.ShouldBe("sub-1");
        summary.ClientId.ShouldBe("cid-1");
        summary.TenantId.ShouldBe("tid-1");
        summary.JwtHasValue.ShouldBe(hasJwt);
    }

    [Fact]
    public void BuildCredentialsSummary_NullCredentials_TokenHasValueFalse()
    {
        var result = DeploymentAccountService.BuildCredentialsSummary(AccountType.Token, null);

        var summary = result.ShouldBeOfType<TokenCredentialsSummary>();
        summary.TokenHasValue.ShouldBeFalse();
    }

    // ========================================================================
    // SerializeEnvironmentIds / DeserializeEnvironmentIds
    // ========================================================================

    [Fact]
    public void SerializeEnvironmentIds_WithValues_SerializesCorrectly()
    {
        DeploymentAccountService.SerializeEnvironmentIds(new List<int> { 1, 3, 5 }).ShouldBe("[1,3,5]");
    }

    [Fact]
    public void SerializeEnvironmentIds_EmptyList_ReturnsNull()
    {
        DeploymentAccountService.SerializeEnvironmentIds(new List<int>()).ShouldBeNull();
    }

    [Fact]
    public void SerializeEnvironmentIds_Null_ReturnsNull()
    {
        DeploymentAccountService.SerializeEnvironmentIds(null).ShouldBeNull();
    }

    [Fact]
    public void DeserializeEnvironmentIds_ValidJson_ReturnsIds()
    {
        DeploymentAccountService.DeserializeEnvironmentIds("[1,3,5]").ShouldBe(new List<int> { 1, 3, 5 });
    }

    [Fact]
    public void DeserializeEnvironmentIds_EmptyString_ReturnsEmptyList()
    {
        DeploymentAccountService.DeserializeEnvironmentIds(string.Empty).ShouldBeEmpty();
    }

    [Fact]
    public void DeserializeEnvironmentIds_Null_ReturnsEmptyList()
    {
        DeploymentAccountService.DeserializeEnvironmentIds(null).ShouldBeEmpty();
    }

    [Fact]
    public void DeserializeEnvironmentIds_MalformedJson_ReturnsEmptyList()
    {
        DeploymentAccountService.DeserializeEnvironmentIds("not-json").ShouldBeEmpty();
    }
}
