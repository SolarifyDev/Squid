using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Account;
using Squid.Core.Services.Authentication;
using Squid.Core.Services.Caching;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Account;
using Squid.Message.Requests.Account;

namespace Squid.UnitTests.Services.Account;

public class AccountServiceTests
{
    private readonly Mock<IRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IUserTokenService> _tokenService = new();
    private readonly Mock<ITeamDataProvider> _teamDataProvider = new();
    private readonly Mock<ICacheManager> _cacheManager = new();
    private readonly AccountService _sut;
    private readonly PasswordHasher<UserAccount> _passwordHasher = new();

    public AccountServiceTests()
    {
        _sut = new AccountService(_repository.Object, _unitOfWork.Object, _tokenService.Object, _teamDataProvider.Object, _cacheManager.Object);
    }

    #region CreateUser

    [Fact]
    public async Task CreateUser_AddsUserToEveryoneTeam()
    {
        _repository.Setup(r => r.AnyAsync<UserAccount>(It.IsAny<Expression<Func<UserAccount, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var everyoneTeam = new Team { Id = 10, Name = "Everyone", SpaceId = 0 };
        _teamDataProvider.Setup(p => p.GetAllBySpaceAsync(0, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team> { everyoneTeam });

        await _sut.CreateUserAsync(new CreateUserCommand { UserName = "testuser", Password = "Test@123456" });

        _teamDataProvider.Verify(p => p.AddMemberAsync(It.Is<TeamMember>(m => m.TeamId == 10), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateUser_NoEveryoneTeam_DoesNotThrow()
    {
        _repository.Setup(r => r.AnyAsync<UserAccount>(It.IsAny<Expression<Func<UserAccount, bool>>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        _teamDataProvider.Setup(p => p.GetAllBySpaceAsync(0, It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team>());

        var result = await _sut.CreateUserAsync(new CreateUserCommand { UserName = "testuser", Password = "Test@123456" });

        result.IsSucceeded.ShouldBeTrue();
        _teamDataProvider.Verify(p => p.AddMemberAsync(It.IsAny<TeamMember>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region ChangePassword

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Throws()
    {
        var user = CreateUserWithPassword("OldPass123");
        SetupFindUser(user);

        var ex = await Should.ThrowAsync<UnauthorizedAccessException>(
            () => _sut.ChangePasswordAsync(user.Id, "WrongPassword", "NewPass123"));

        ex.Message.ShouldContain("Current password is incorrect");
    }

    [Fact]
    public async Task ChangePassword_ValidPassword_UpdatesHash()
    {
        var user = CreateUserWithPassword("OldPass123");
        SetupFindUser(user);

        await _sut.ChangePasswordAsync(user.Id, "OldPass123", "NewPass123");

        _repository.Verify(r => r.UpdateAsync(It.Is<UserAccount>(u => u.Id == user.Id), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData(null)]
    public async Task ChangePassword_NewPasswordTooShort_Throws(string? newPassword)
    {
        var ex = await Should.ThrowAsync<ArgumentException>(
            () => _sut.ChangePasswordAsync(1, "OldPass123", newPassword!));

        ex.Message.ShouldContain("at least 6 characters");
    }

    [Fact]
    public async Task ChangePassword_Success_ClearsMustChangePassword()
    {
        var user = CreateUserWithPassword("OldPass123");
        user.MustChangePassword = true;
        SetupFindUser(user);

        await _sut.ChangePasswordAsync(user.Id, "OldPass123", "NewPass123");

        user.MustChangePassword.ShouldBeFalse();
    }

    [Fact]
    public async Task ChangePassword_UserNotFound_Throws()
    {
        _repository.Setup(r => r.FirstOrDefaultAsync<UserAccount>(It.IsAny<Expression<Func<UserAccount, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAccount?)null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.ChangePasswordAsync(999, "OldPass123", "NewPass123"));

        ex.Message.ShouldContain("not found");
    }

    #endregion

    #region UpdateUserStatus (Disable/Enable)

    [Fact]
    public async Task DisableUser_DisableSelf_Throws()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.UpdateUserStatusAsync(userId: 1, isDisabled: true, currentUserId: 1));

        ex.Message.ShouldContain("Cannot disable your own account");
    }

    [Fact]
    public async Task DisableUser_DisableSystemUser_Throws()
    {
        var user = new UserAccount { Id = 2, IsSystem = true, UserName = "system" };
        SetupFindUser(user);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.UpdateUserStatusAsync(userId: 2, isDisabled: true, currentUserId: 1));

        ex.Message.ShouldContain("Cannot disable a system user");
    }

    [Fact]
    public async Task DisableUser_ClearsApiKeyCache()
    {
        var user = new UserAccount { Id = 2, IsSystem = false, UserName = "bob" };
        SetupFindUser(user);

        var keys = new List<UserAccountApiKey>
        {
            new() { Id = 1, UserAccountId = 2, ApiKey = "key-abc-123", IsDisabled = false },
            new() { Id = 2, UserAccountId = 2, ApiKey = "key-def-456", IsDisabled = false },
        };
        _repository.Setup(r => r.Query<UserAccountApiKey>(It.IsAny<Expression<Func<UserAccountApiKey, bool>>>()))
            .Returns(keys.AsQueryable().BuildMock());

        await _sut.UpdateUserStatusAsync(userId: 2, isDisabled: true, currentUserId: 1);

        _cacheManager.Verify(c => c.RemoveAsync("auth:apikey:user:key-abc-123", It.IsAny<ICachingSetting>(), It.IsAny<CancellationToken>()), Times.Once);
        _cacheManager.Verify(c => c.RemoveAsync("auth:apikey:user:key-def-456", It.IsAny<ICachingSetting>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnableUser_DoesNotClearApiKeyCache()
    {
        var user = new UserAccount { Id = 2, IsSystem = false, UserName = "bob", IsDisabled = true };
        SetupFindUser(user);

        await _sut.UpdateUserStatusAsync(userId: 2, isDisabled: false, currentUserId: 1);

        _cacheManager.Verify(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<ICachingSetting>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisableUser_UserNotFound_Throws()
    {
        _repository.Setup(r => r.FirstOrDefaultAsync<UserAccount>(It.IsAny<Expression<Func<UserAccount, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAccount?)null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.UpdateUserStatusAsync(userId: 999, isDisabled: true, currentUserId: 1));

        ex.Message.ShouldContain("not found");
    }

    #endregion

    #region ApiKey CRUD

    [Fact]
    public async Task CreateApiKey_ReturnsPlaintextKey()
    {
        var result = await _sut.CreateApiKeyAsync(userId: 1, description: "CI pipeline");

        result.ApiKey.ShouldNotBeNullOrWhiteSpace();
        result.ApiKey.Length.ShouldBeGreaterThan(8);
        result.Description.ShouldBe("CI pipeline");
        _repository.Verify(r => r.InsertAsync(It.Is<UserAccountApiKey>(k => k.UserAccountId == 1 && !k.IsDisabled), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateApiKey_GeneratesUniqueKeys()
    {
        var result1 = await _sut.CreateApiKeyAsync(userId: 1, description: "key1");
        var result2 = await _sut.CreateApiKeyAsync(userId: 1, description: "key2");

        result1.ApiKey.ShouldNotBe(result2.ApiKey);
    }

    [Fact]
    public async Task DeleteApiKey_DisablesAndClearsCache()
    {
        var apiKey = new UserAccountApiKey { Id = 5, UserAccountId = 1, ApiKey = "raw-key-value", IsDisabled = false };
        _repository.Setup(r => r.FirstOrDefaultAsync<UserAccountApiKey>(It.IsAny<Expression<Func<UserAccountApiKey, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiKey);

        await _sut.DeleteApiKeyAsync(apiKeyId: 5, currentUserId: 1);

        apiKey.IsDisabled.ShouldBeTrue();
        _repository.Verify(r => r.UpdateAsync(It.Is<UserAccountApiKey>(k => k.Id == 5 && k.IsDisabled), It.IsAny<CancellationToken>()), Times.Once);
        _cacheManager.Verify(c => c.RemoveAsync("auth:apikey:user:raw-key-value", It.IsAny<ICachingSetting>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteApiKey_NotFound_Throws()
    {
        _repository.Setup(r => r.FirstOrDefaultAsync<UserAccountApiKey>(It.IsAny<Expression<Func<UserAccountApiKey, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAccountApiKey?)null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.DeleteApiKeyAsync(apiKeyId: 999, currentUserId: 1));

        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task GetApiKeys_ReturnsMaskedKeys()
    {
        var keys = new List<UserAccountApiKey>
        {
            new() { Id = 1, UserAccountId = 1, ApiKey = "abcdefghijklmnop", Description = "Test key", IsDisabled = false, CreatedDate = DateTimeOffset.UtcNow },
        };
        _repository.Setup(r => r.Query<UserAccountApiKey>(It.IsAny<Expression<Func<UserAccountApiKey, bool>>>()))
            .Returns(keys.AsQueryable().BuildMock());

        var result = await _sut.GetApiKeysAsync(userId: 1);

        result.Count.ShouldBe(1);
        result[0].MaskedKey.ShouldBe("abcd****mnop");
        result[0].Description.ShouldBe("Test key");
    }

    #endregion

    #region Login — MustChangePassword

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Login_ReturnsMustChangePasswordFlag(bool mustChange)
    {
        var user = CreateUserWithPassword("Test@123456");
        user.MustChangePassword = mustChange;

        _repository.Setup(r => r.FirstOrDefaultAsync<UserAccount>(It.IsAny<Expression<Func<UserAccount, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _tokenService.Setup(t => t.GenerateToken(It.IsAny<UserAccount>()))
            .Returns(("fake-token", DateTime.UtcNow.AddHours(1)));

        var result = await _sut.LoginAsync(new LoginRequest { UserName = user.UserName, Password = "Test@123456" });

        result.MustChangePassword.ShouldBe(mustChange);
    }

    #endregion

    #region Helpers

    private UserAccount CreateUserWithPassword(string password)
    {
        var user = new UserAccount
        {
            Id = 1,
            UserName = "testuser",
            NormalizedUserName = "TESTUSER",
            DisplayName = "Test User",
            IsDisabled = false,
            IsSystem = false,
        };

        user.PasswordHash = _passwordHasher.HashPassword(user, password);

        return user;
    }

    private void SetupFindUser(UserAccount user)
    {
        _repository.Setup(r => r.FirstOrDefaultAsync<UserAccount>(It.IsAny<Expression<Func<UserAccount, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
    }

    #endregion
}
