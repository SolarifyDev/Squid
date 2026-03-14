using Microsoft.AspNetCore.Identity;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authentication;
using Squid.Message.Commands.Account;
using Squid.Message.Models.Account;
using Squid.Message.Requests.Account;

namespace Squid.Core.Services.Account;

public interface IAccountService : IScopedDependency
{
    Task<RegisterResponseData> RegisterAsync(RegisterCommand command, CancellationToken cancellationToken = default);

    Task<LoginResponseData> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<UserAccountDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<UserAccountDto?> GetByApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);
}

public class AccountService : IAccountService
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserTokenService _userTokenService;
    private readonly PasswordHasher<UserAccount> _passwordHasher = new();

    public AccountService(IRepository repository, IUnitOfWork unitOfWork, IUserTokenService userTokenService)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _userTokenService = userTokenService;
    }

    public async Task<RegisterResponseData> RegisterAsync(RegisterCommand command, CancellationToken cancellationToken = default)
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

        return new RegisterResponseData
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
            UserAccount = ToDto(user)
        };
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
