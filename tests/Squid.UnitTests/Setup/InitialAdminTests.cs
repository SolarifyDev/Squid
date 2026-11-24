using System.IO;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Settings.Setup;
using Squid.Message.Constants;
using Squid.Message.Models.Setup;

namespace Squid.UnitTests.Setup;

public sealed class InitialAdminTests
{
    [Fact]
    public void NoBootstrap_NoDataDirectory_KeepsHistoricalDefault()
    {
        var admin = Resolve(new BootstrapAdminSetting(), new SetupAdminSetting(), dataDirectory: null);

        admin.ShouldBe(new InitialAdmin(CurrentUsers.AdminUser.UserName, CurrentUsers.AdminUser.DefaultPassword, null, true, null));
    }

    [Fact]
    public void WizardAccount_WinsAsAPair_OverStartParameters()
    {
        // SQUID_ADMIN_USER outranks squid.json; it must not rename the account the wizard created.
        var admin = Resolve(new BootstrapAdminSetting { UserName = "from-env", Password = "env-pass" }, new SetupAdminSetting { UserName = " owner ", PasswordHash = "HASH" }, dataDirectory: "/data");

        admin.ShouldBe(new InitialAdmin("owner", null, "HASH", false, null));
    }

    [Fact]
    public void IncompleteWizardPair_IsIgnored()
    {
        var admin = Resolve(new BootstrapAdminSetting { Password = "env-pass" }, new SetupAdminSetting { UserName = "owner" }, dataDirectory: "/data");

        admin.ShouldBe(new InitialAdmin(CurrentUsers.AdminUser.UserName, "env-pass", null, false, null));
    }

    [Fact]
    public void PlainStartParameter_IsUsed_AndNeedsNoChange()
    {
        var admin = Resolve(new BootstrapAdminSetting { UserName = "ops", Password = "plain-pass" }, new SetupAdminSetting(), dataDirectory: "/data");

        admin.ShouldBe(new InitialAdmin("ops", "plain-pass", null, false, null));
    }

    [Fact]
    public void SelfContainedInstall_WithoutPassword_GetsRandomOne_NeverTheWellKnownDefault()
    {
        var admin = Resolve(new BootstrapAdminSetting(), new SetupAdminSetting(), dataDirectory: "/data");

        admin.Password.ShouldNotBe(CurrentUsers.AdminUser.DefaultPassword);
        admin.Password.Length.ShouldBe(20);
        admin.MustChangePassword.ShouldBeTrue();
        admin.GeneratedPasswordFile.ShouldBe(Path.Combine("/data", AdminUserSeeder.InitialPasswordFileName));
    }

    private static InitialAdmin Resolve(BootstrapAdminSetting bootstrap, SetupAdminSetting setupAdmin, string dataDirectory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DataDirectory"] = dataDirectory }).Build();

        return AdminUserSeeder.ResolveInitialAdmin(bootstrap, setupAdmin, new DataDirectorySetting(configuration));
    }
}
