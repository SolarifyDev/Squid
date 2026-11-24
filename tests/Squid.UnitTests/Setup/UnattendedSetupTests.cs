using Squid.Core.Setup;
using Squid.Message.Models.Setup;

namespace Squid.UnitTests.Setup;

public sealed class UnattendedSetupTests
{
    [Fact]
    public void BuildsBuiltinDatabaseRequest_FromStartParameters()
    {
        using var dir = new TempDataDirectory();
        var configuration = dir.Configuration(new() { ["Bootstrap:Admin:Password"] = "longenough", ["ServerUrl:ExternalUrl"] = "https://squid.example.com", ["EmbeddedServices:Redis:Enabled"] = "true" });

        var request = UnattendedSetup.BuildRequest(configuration);

        request.Database.Mode.ShouldBe(SetupServiceMode.Builtin);
        request.Redis.Mode.ShouldBe(SetupServiceMode.Builtin);
        request.Admin.UserName.ShouldBe(UnattendedSetup.DefaultAdminUserName);
        request.Admin.Password.ShouldBe("longenough");
        request.Server.ExternalUrl.ShouldBe("https://squid.example.com");
    }

    [Fact]
    public void ExternalRedisAndCustomUser_AreCarriedOver_AndUrlDefaults()
    {
        using var dir = new TempDataDirectory();
        var configuration = dir.Configuration(new() { ["Bootstrap:Admin:UserName"] = "ops", ["Bootstrap:Admin:Password"] = "longenough", ["RedisCacheConnectionString"] = "redis:6379" });

        var request = UnattendedSetup.BuildRequest(configuration);

        request.Redis.ShouldBe(new SetupRedisChoice { Mode = SetupServiceMode.External, ConnectionString = "redis:6379" });
        request.Admin.UserName.ShouldBe("ops");
        request.Server.ExternalUrl.ShouldBe(UnattendedSetup.DefaultExternalUrl);
    }
}
