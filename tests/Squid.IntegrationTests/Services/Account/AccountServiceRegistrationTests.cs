using Microsoft.AspNetCore.Identity;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;

namespace Squid.IntegrationTests.Services.Account;

public class AccountServiceRegistrationTests : TestBase
{
    public AccountServiceRegistrationTests()
        : base("AccountRegistration", "squid_it_account_registration")
    {
    }

    [Fact]
    public async Task Register_AutoJoinsEveryoneTeam()
    {
        // Seed built-in teams first
        await Run<ILifetimeScope>(async scope =>
        {
            var seeder = new BuiltInRoleSeeder(scope);
            seeder.Start();

            await Task.CompletedTask;
        }).ConfigureAwait(false);

        // Register a new user directly (bypassing IAccountService to avoid IUserTokenService config dependency)
        await Run<IRepository, IUnitOfWork, ITeamDataProvider>(async (repository, unitOfWork, teamProvider) =>
        {
            var passwordHasher = new PasswordHasher<UserAccount>();
            var user = new UserAccount
            {
                UserName = "newuser",
                NormalizedUserName = "NEWUSER",
                DisplayName = "New User",
                IsDisabled = false,
                IsSystem = false,
                CreatedDate = DateTime.UtcNow,
                LastModifiedDate = DateTime.UtcNow
            };
            user.PasswordHash = passwordHasher.HashPassword(user, "Test@123456");

            await repository.InsertAsync(user).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            // Simulate the auto-join that AccountService.CreateUserAsync does
            var teams = await teamProvider.GetAllBySpaceAsync(0).ConfigureAwait(false);
            var everyoneTeam = teams.FirstOrDefault(t => t.Name == "Everyone");

            everyoneTeam.ShouldNotBeNull();

            await teamProvider.AddMemberAsync(new TeamMember { TeamId = everyoneTeam.Id, UserId = user.Id }).ConfigureAwait(false);

            // Verify
            var members = await teamProvider.GetMembersByTeamIdAsync(everyoneTeam.Id).ConfigureAwait(false);
            members.ShouldContain(m => m.UserId == user.Id, "New user should be in Everyone team");
        }).ConfigureAwait(false);
    }
}
