using Shouldly;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Constants;
using Xunit;

namespace Squid.E2ETests.Authentication;

[Trait("Category", "E2E")]
public class AuthenticationFixture
    : IClassFixture<AuthenticationE2EFixture<AuthenticationFixture>>
{
    private readonly AuthenticationE2EFixture<AuthenticationFixture> _fixture;

    public AuthenticationFixture(AuthenticationE2EFixture<AuthenticationFixture> fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ShouldRegisterAndLoginSuccessfully()
    {
        var register = await _fixture.CreateUserAsync(
            userName: "auth-fixture-user",
            password: "123456",
            displayName: "Auth Fixture User");

        register.IsSucceeded.ShouldBeTrue();
        register.UserAccount.UserName.ShouldBe("auth-fixture-user");

        var login = await _fixture.LoginAsync("auth-fixture-user", "123456");

        login.AccessToken.ShouldNotBeNullOrWhiteSpace();
        login.UserAccount.UserName.ShouldBe("auth-fixture-user");
    }

    [Fact]
    public async Task ShouldResolveUserByApiKeyFromDatabase()
    {
        var register = await _fixture.CreateUserAsync(
            userName: "apikey-user",
            password: "123456");

        await _fixture.AddApiKeyAsync(register.UserAccount.Id, "apikey-user-key");

        var user = await _fixture.GetUserByApiKeyAsync("apikey-user-key");

        user.ShouldNotBeNull();
        user.UserName.ShouldBe("apikey-user");
    }

    [Fact]
    public async Task ShouldReturnNullWhenApiKeyNotExists()
    {
        var user = await _fixture.GetUserByApiKeyAsync("missing-api-key");

        user.ShouldBeNull();
    }

    [Fact]
    public async Task ShouldFallbackToInternalCurrentUserWhenNoHttpContext()
    {
        var currentUser = await _fixture.GetCurrentUserSnapshotAsync();

        currentUser.Id.ShouldBe(CurrentUsers.InternalUser.Id);
        currentUser.Name.ShouldBe(CurrentUsers.InternalUser.Name);
    }

    [Fact]
    public async Task ShouldSeedInternalUserRecordWithDefaultId()
    {
        var user = await _fixture.GetUserByIdAsync(CurrentUsers.InternalUser.Id);

        user.ShouldNotBeNull();
        user.Id.ShouldBe(CurrentUsers.InternalUser.Id);
        user.UserName.ShouldBe(CurrentUsers.InternalUser.Name);
        user.IsSystem.ShouldBeTrue();
    }
}
