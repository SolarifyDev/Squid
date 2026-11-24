using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.Teams;
using Squid.Core.Settings.Setup;

namespace Squid.IntegrationTests.Setup;

/// <summary>
/// The first administrator against a real database: created from the wizard's account or a
/// generated password on a fresh install only, placed in the administrators team, and never
/// re-created once it has been removed (re-creating would silently re-open the account with its
/// bootstrap password).
/// </summary>
public class AdminUserSeederIntegrationTests : TestBase, IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"squid-admin-it-{Guid.NewGuid():N}");

    public AdminUserSeederIntegrationTests() : base("AdminUserSeeder", "squid_it_admin_user_seeder")
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    [Fact]
    public async Task WizardAccount_FreshInstall_IsCreatedAsAdministrator_WithoutForcedPasswordChange()
    {
        var hash = new PasswordHasher<UserAccount>().HashPassword(new UserAccount(), "Wizard-Pass-1");

        await SeedPrerequisitesAsync();
        await SeedAdminAsync(new BootstrapAdminSetting { UserName = "from-env" }, new SetupAdminSetting { UserName = "owner", PasswordHash = hash });

        var user = await FindUserAsync("owner");

        user.ShouldNotBeNull();
        user.MustChangePassword.ShouldBeFalse();
        (await FindUserAsync("from-env")).ShouldBeNull("a start parameter must not rename the wizard's account");
        (await IsAdministratorAsync(user.Id)).ShouldBeTrue();
        new PasswordHasher<UserAccount>().VerifyHashedPassword(user, user.PasswordHash, "Wizard-Pass-1").ShouldNotBe(PasswordVerificationResult.Failed);
    }

    [Fact]
    public async Task GeneratedPassword_IsHandedOverThroughTheDataDirectory_AndOpensTheAccount()
    {
        await SeedPrerequisitesAsync();
        await SeedAdminAsync(new BootstrapAdminSetting(), new SetupAdminSetting(), _dataDirectory);

        var password = File.ReadAllText(Path.Combine(_dataDirectory, AdminUserSeeder.InitialPasswordFileName)).Trim();
        var user = await FindUserAsync("admin");

        user.MustChangePassword.ShouldBeTrue();
        new PasswordHasher<UserAccount>().VerifyHashedPassword(user, user.PasswordHash, password).ShouldNotBe(PasswordVerificationResult.Failed);
    }

    [Fact]
    public async Task RemovedAdministrator_IsNotRecreatedOnTheNextStart()
    {
        await SeedPrerequisitesAsync();
        await SeedAdminAsync(new BootstrapAdminSetting { Password = "Bootstrap-Pass-1" }, new SetupAdminSetting());
        await RenameUserAsync("admin", "renamed-admin");

        await SeedAdminAsync(new BootstrapAdminSetting { Password = "Bootstrap-Pass-1" }, new SetupAdminSetting());

        (await FindUserAsync("admin")).ShouldBeNull("re-creating the bootstrap admin would re-open an account the operator removed");
        (await FindUserAsync("renamed-admin")).ShouldNotBeNull();
    }

    public new void Dispose()
    {
        base.Dispose();

        try { Directory.Delete(_dataDirectory, true); } catch (IOException) { }
    }

    private Task SeedPrerequisitesAsync() => Run<ILifetimeScope>(async scope =>
    {
        await new BuiltInRoleSeeder().SeedAsync(scope).ConfigureAwait(false);
        await new BuiltInTeamSeeder().SeedAsync(scope).ConfigureAwait(false);
    });

    private Task SeedAdminAsync(BootstrapAdminSetting bootstrap, SetupAdminSetting setupAdmin, string dataDirectory = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataDirectory"] = dataDirectory }).Build();

        return Run<ILifetimeScope>(scope => new AdminUserSeeder(bootstrap, setupAdmin, new DataDirectorySetting(configuration)).SeedAsync(scope));
    }

    private Task<UserAccount> FindUserAsync(string userName) => Run<IRepository, UserAccount>(repository => repository.FirstOrDefaultAsync<UserAccount>(u => u.NormalizedUserName == userName.ToUpperInvariant()));

    private Task RenameUserAsync(string from, string to) => Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
    {
        var user = await repository.FirstOrDefaultAsync<UserAccount>(u => u.NormalizedUserName == from.ToUpperInvariant()).ConfigureAwait(false);

        user.UserName = to;
        user.NormalizedUserName = to.ToUpperInvariant();

        await repository.UpdateAsync(user).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
    });

    private Task<bool> IsAdministratorAsync(int userId) => Run<ITeamDataProvider, bool>(async teams =>
    {
        var administrators = (await teams.GetAllBySpaceAsync(0).ConfigureAwait(false)).Single(t => t.Name == BuiltInTeamNames.Administrators);
        var members = await teams.GetMembersByTeamIdAsync(administrators.Id).ConfigureAwait(false);

        return members.Any(m => m.UserId == userId);
    });
}
