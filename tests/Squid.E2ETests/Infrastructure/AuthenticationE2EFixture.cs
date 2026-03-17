using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Account;
using Squid.Core.Services.Identity;
using Squid.E2ETests.Infrastructure;
using Squid.Message.Commands.Account;
using Squid.Message.Requests.Account;

namespace Squid.E2ETests.Infrastructure;

public class AuthenticationE2EFixture<TTestClass> : E2EFixtureBase<TTestClass>
{
    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        configuration["Authentication:Jwt:SymmetricKey"] = "squid-e2e-auth-fixture-secret-key";

        builder.RegisterType<HttpContextAccessor>()
            .As<IHttpContextAccessor>()
            .SingleInstance();
    }

    public Task<CreateUserResponseData> CreateUserAsync(
        string userName = "e2e-user",
        string password = "123456",
        string? displayName = null)
    {
        return Run<IAccountService, CreateUserResponseData>(service =>
            service.CreateUserAsync(new CreateUserCommand
            {
                UserName = userName,
                Password = password,
                DisplayName = displayName
            }));
    }

    public Task<LoginResponseData> LoginAsync(string userName = "e2e-user", string password = "123456")
    {
        return Run<IAccountService, LoginResponseData>(service =>
            service.LoginAsync(new LoginRequest
            {
                UserName = userName,
                Password = password
            }));
    }

    public async Task AddApiKeyAsync(int userId, string apiKey = "e2e-api-key", string? description = null)
    {
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            await repository.InsertAsync(new UserAccountApiKey
            {
                UserAccountId = userId,
                ApiKey = apiKey,
                Description = description,
                IsDisabled = false,
                CreatedDate = DateTimeOffset.UtcNow
            }).ConfigureAwait(false);

            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public Task<Squid.Message.Models.Account.UserAccountDto?> GetUserByApiKeyAsync(string apiKey)
    {
        return Run<IAccountService, Squid.Message.Models.Account.UserAccountDto?>(service =>
            service.GetByApiKeyAsync(apiKey));
    }

    public Task<Squid.Message.Models.Account.UserAccountDto?> GetUserByIdAsync(int id)
    {
        return Run<IAccountService, Squid.Message.Models.Account.UserAccountDto?>(service =>
            service.GetByIdAsync(id));
    }

    public Task<(int? Id, string Name)> GetCurrentUserSnapshotAsync()
    {
        return Run<ICurrentUser, (int? Id, string Name)>(currentUser =>
            Task.FromResult((currentUser.Id, currentUser.Name)));
    }
}
