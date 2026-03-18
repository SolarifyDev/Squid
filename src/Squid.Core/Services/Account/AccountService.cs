using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authentication;
using Squid.Core.Services.Caching;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Account;
using Squid.Message.Models.Account;
using Squid.Message.Requests.Account;

namespace Squid.Core.Services.Account;

public interface IAccountService : IScopedDependency
{
    Task<CreateUserResponseData> CreateUserAsync(CreateUserCommand command, CancellationToken cancellationToken = default);
    Task<LoginResponseData> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<List<UserAccountDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<UserAccountDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<UserAccountDto?> GetByApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);
    Task ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default);
    Task UpdateUserStatusAsync(int userId, bool isDisabled, int currentUserId, CancellationToken ct = default);
    Task<CreateApiKeyResponseData> CreateApiKeyAsync(int userId, string description, CancellationToken ct = default);
    Task DeleteApiKeyAsync(int apiKeyId, int currentUserId, CancellationToken ct = default);
    Task<List<ApiKeyDto>> GetApiKeysAsync(int userId, CancellationToken ct = default);
}

public class AccountService : IAccountService
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserTokenService _userTokenService;
    private readonly ITeamDataProvider _teamDataProvider;
    private readonly ICacheManager _cacheManager;
    private readonly PasswordHasher<UserAccount> _passwordHasher = new();

    public AccountService(IRepository repository, IUnitOfWork unitOfWork, IUserTokenService userTokenService, ITeamDataProvider teamDataProvider, ICacheManager cacheManager)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _userTokenService = userTokenService;
        _teamDataProvider = teamDataProvider;
        _cacheManager = cacheManager;
    }

    public async Task<CreateUserResponseData> CreateUserAsync(CreateUserCommand command, CancellationToken cancellationToken = default)
    {
        var userName = NormalizeUserNameInput(command.UserName);
        var password = command.Password;

        if (string.IsNullOrWhiteSpace(userName))
            throw new ArgumentException("UserName is required");

        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password is required");

        if (password.Length < 6)
            throw new ArgumentException("Password length must be at least 6 characters");

        var normalizedUserName = userName.ToUpperInvariant();
        var exists = await _repository.AnyAsync<UserAccount>(x => x.NormalizedUserName == normalizedUserName, cancellationToken).ConfigureAwait(false);

        if (exists)
            throw new ArgumentException($"User '{userName}' already exists");

        var displayName = string.IsNullOrWhiteSpace(command.DisplayName) ? userName : command.DisplayName.Trim();

        var user = new UserAccount
        {
            UserName = userName,
            NormalizedUserName = normalizedUserName,
            DisplayName = displayName,
            IsDisabled = false,
            IsSystem = false,
            CreatedDate = DateTime.UtcNow,
            LastModifiedDate = DateTime.UtcNow
        };

        user.PasswordHash = _passwordHasher.HashPassword(user, password);

        await _repository.InsertAsync(user, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await AddToEveryoneTeamAsync(user.Id, cancellationToken).ConfigureAwait(false);

        return new CreateUserResponseData
        {
            IsSucceeded = true,
            UserAccount = ToDto(user)
        };
    }

    public async Task<LoginResponseData> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var userName = NormalizeUserNameInput(request.UserName);
        var password = request.Password;

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            throw new UnauthorizedAccessException("Invalid username or password");

        var user = await _repository.FirstOrDefaultAsync<UserAccount>(
            x => x.NormalizedUserName == userName.ToUpperInvariant(), cancellationToken).ConfigureAwait(false);

        if (user == null || user.IsDisabled)
            throw new UnauthorizedAccessException("Invalid username or password");

        var verifyResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);

        if (verifyResult == PasswordVerificationResult.Failed)
            throw new UnauthorizedAccessException("Invalid username or password");

        if (verifyResult == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _passwordHasher.HashPassword(user, password);
            user.LastModifiedDate = DateTime.UtcNow;
            await _repository.UpdateAsync(user, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var token = _userTokenService.GenerateToken(user);

        return new LoginResponseData
        {
            AccessToken = token.AccessToken,
            ExpiresAtUtc = token.ExpiresAtUtc,
            UserAccount = ToDto(user),
            MustChangePassword = user.MustChangePassword
        };
    }

    public async Task<List<UserAccountDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var users = await _repository.Query<UserAccount>(x => !x.IsSystem).ToListAsync(cancellationToken).ConfigureAwait(false);

        return users.Select(ToDto).ToList();
    }

    public async Task<UserAccountDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var user = await _repository.FirstOrDefaultAsync<UserAccount>(x => x.Id == id, cancellationToken).ConfigureAwait(false);

        if (user == null || user.IsDisabled) return null;

        return ToDto(user);
    }

    public async Task<UserAccountDto?> GetByApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var apiKeyEntity = await _repository.FirstOrDefaultAsync<UserAccountApiKey>(
            x => x.ApiKey == apiKey && !x.IsDisabled, cancellationToken).ConfigureAwait(false);

        if (apiKeyEntity == null) return null;

        return await GetByIdAsync(apiKeyEntity.UserAccountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            throw new ArgumentException("New password must be at least 6 characters");

        var user = await _repository.FirstOrDefaultAsync<UserAccount>(x => x.Id == userId, ct).ConfigureAwait(false);

        if (user == null)
            throw new InvalidOperationException($"User {userId} not found");

        var verifyResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword);

        if (verifyResult == PasswordVerificationResult.Failed)
            throw new UnauthorizedAccessException("Current password is incorrect");

        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
        user.MustChangePassword = false;
        user.LastModifiedDate = DateTime.UtcNow;

        await _repository.UpdateAsync(user, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateUserStatusAsync(int userId, bool isDisabled, int currentUserId, CancellationToken ct = default)
    {
        if (userId == currentUserId)
            throw new InvalidOperationException("Cannot disable your own account");

        var user = await _repository.FirstOrDefaultAsync<UserAccount>(x => x.Id == userId, ct).ConfigureAwait(false);

        if (user == null)
            throw new InvalidOperationException($"User {userId} not found");

        if (user.IsSystem)
            throw new InvalidOperationException("Cannot disable a system user");

        user.IsDisabled = isDisabled;
        user.LastModifiedDate = DateTime.UtcNow;

        await _repository.UpdateAsync(user, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        if (isDisabled)
            await InvalidateApiKeyCacheForUserAsync(userId, ct).ConfigureAwait(false);
    }

    public async Task<CreateApiKeyResponseData> CreateApiKeyAsync(int userId, string description, CancellationToken ct = default)
    {
        var rawKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var entity = new UserAccountApiKey
        {
            UserAccountId = userId,
            ApiKey = rawKey,
            Description = description,
            IsDisabled = false,
            CreatedDate = DateTimeOffset.UtcNow,
            LastModifiedDate = DateTimeOffset.UtcNow
        };

        await _repository.InsertAsync(entity, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        return new CreateApiKeyResponseData
        {
            Id = entity.Id,
            ApiKey = rawKey,
            Description = description
        };
    }

    public async Task DeleteApiKeyAsync(int apiKeyId, int currentUserId, CancellationToken ct = default)
    {
        var apiKey = await _repository.FirstOrDefaultAsync<UserAccountApiKey>(x => x.Id == apiKeyId, ct).ConfigureAwait(false);

        if (apiKey == null)
            throw new InvalidOperationException($"API Key {apiKeyId} not found");

        apiKey.IsDisabled = true;
        apiKey.LastModifiedDate = DateTimeOffset.UtcNow;

        await _repository.UpdateAsync(apiKey, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var cacheKey = $"auth:apikey:user:{apiKey.ApiKey}";
        await _cacheManager.RemoveAsync(cacheKey, new RedisCachingSetting(), ct).ConfigureAwait(false);
    }

    public async Task<List<ApiKeyDto>> GetApiKeysAsync(int userId, CancellationToken ct = default)
    {
        var keys = await _repository.Query<UserAccountApiKey>(x => x.UserAccountId == userId && !x.IsDisabled)
            .ToListAsync(ct).ConfigureAwait(false);

        return keys.Select(k => new ApiKeyDto
        {
            Id = k.Id,
            MaskedKey = MaskApiKey(k.ApiKey),
            Description = k.Description,
            CreatedDate = k.CreatedDate
        }).ToList();
    }

    private async Task InvalidateApiKeyCacheForUserAsync(int userId, CancellationToken ct)
    {
        var keys = await _repository.Query<UserAccountApiKey>(x => x.UserAccountId == userId && !x.IsDisabled)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var key in keys)
        {
            var cacheKey = $"auth:apikey:user:{key.ApiKey}";
            await _cacheManager.RemoveAsync(cacheKey, new RedisCachingSetting(), ct).ConfigureAwait(false);
        }
    }

    private static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length <= 8) return "****";
        return string.Concat(apiKey.AsSpan(0, 4), "****", apiKey.AsSpan(apiKey.Length - 4));
    }

    private async Task AddToEveryoneTeamAsync(int userId, CancellationToken ct)
    {
        var teams = await _teamDataProvider.GetAllBySpaceAsync(0, ct).ConfigureAwait(false);
        var everyoneTeam = teams.FirstOrDefault(t => t.Name == "Everyone");

        if (everyoneTeam == null) return;

        await _teamDataProvider.AddMemberAsync(new TeamMember { TeamId = everyoneTeam.Id, UserId = userId }, ct: ct).ConfigureAwait(false);
    }

    private static string NormalizeUserNameInput(string? userName)
    {
        return (userName ?? string.Empty).Trim();
    }

    private static UserAccountDto ToDto(UserAccount user)
    {
        return new UserAccountDto
        {
            Id = user.Id,
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            IsSystem = user.IsSystem,
            CreatedDate = user.CreatedDate
        };
    }
}
