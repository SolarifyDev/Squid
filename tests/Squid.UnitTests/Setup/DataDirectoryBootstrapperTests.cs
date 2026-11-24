using System.IO;
using Microsoft.Extensions.Configuration;
using Squid.Core.Setup;
using Squid.Core.Setup.Configuration;

namespace Squid.UnitTests.Setup;

public sealed class DataDirectoryBootstrapperTests
{
    [Fact]
    public void NoDataDirectory_DoesNothing()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build();

        DataDirectoryBootstrapper.Run(configuration).ShouldBeFalse();
    }

    [Fact]
    public void FirstRun_PersistsSecrets_SecondRunIsANoOp()
    {
        using var dir = new TempDataDirectory();

        DataDirectoryBootstrapper.Run(dir.Configuration(new() { ["RedisCacheConnectionString"] = "r:1" })).ShouldBeTrue();

        var reloaded = Reload(dir, new() { ["RedisCacheConnectionString"] = "r:1" });

        reloaded["Security:VariableEncryption:MasterKey"].ShouldNotBeNullOrWhiteSpace();
        reloaded["SelfCert:Base64"].ShouldNotBeNullOrWhiteSpace();

        DataDirectoryBootstrapper.Run(reloaded).ShouldBeFalse();
    }

    [Fact]
    public void NoRedisConfigured_AndRedisShipped_SwitchesOnBuiltinRedis()
    {
        using var dir = new TempDataDirectory();
        var fakeRedis = FakeExecutable(dir);

        DataDirectoryBootstrapper.Run(dir.Configuration(new() { ["EmbeddedServices:Redis:ServerPath"] = fakeRedis }));

        SquidConfigurationFile.GetValue(dir.ConfigFile, "EmbeddedServices:Redis:Enabled").ShouldBe("true");
        SquidConfigurationFile.GetValue(dir.ConfigFile, "RedisCacheConnectionString").ShouldStartWith("127.0.0.1:6379,password=");
    }

    [Fact]
    public void NoRedisConfigured_AndRedisNotShipped_LeavesRedisToTheSetup()
    {
        using var dir = new TempDataDirectory();

        DataDirectoryBootstrapper.Run(dir.Configuration(new() { ["EmbeddedServices:Redis:ServerPath"] = Path.Combine(dir.Path, "missing") }));

        SquidConfigurationFile.GetValue(dir.ConfigFile, "EmbeddedServices:Redis:Enabled").ShouldBeNull();
    }

    [Fact]
    public void RedisAlreadyConfigured_IsKept_NotReplacedByBuiltin()
    {
        using var dir = new TempDataDirectory();

        DataDirectoryBootstrapper.Run(dir.Configuration(new() { ["RedisCacheConnectionString"] = "external:6379", ["EmbeddedServices:Redis:ServerPath"] = FakeExecutable(dir) }));

        SquidConfigurationFile.GetValue(dir.ConfigFile, "RedisCacheConnectionString").ShouldBe("external:6379");
        SquidConfigurationFile.GetValue(dir.ConfigFile, "EmbeddedServices:Redis:Enabled").ShouldBeNull();
    }

    [Fact]
    public void ConnectionStringsFromStartParameters_AreRemembered_SoAnUpgradeWithoutThemStillFindsTheDatabase()
    {
        using var dir = new TempDataDirectory();

        DataDirectoryBootstrapper.Run(dir.Configuration(new() { ["SquidStore:ConnectionString"] = "Host=db;Database=squid", ["RedisCacheConnectionString"] = "redis:6379" }));

        SquidConfigurationFile.GetValue(dir.ConfigFile, "SquidStore:ConnectionString").ShouldBe("Host=db;Database=squid");
        SquidConfigurationFile.GetValue(dir.ConfigFile, "RedisCacheConnectionString").ShouldBe("redis:6379");

        Squid.Core.Setup.SetupRequirement.IsRequired(Reload(dir, new())).ShouldBeFalse();
    }

    private static string FakeExecutable(TempDataDirectory dir)
    {
        var path = Path.Combine(dir.Path, "redis-server");

        File.WriteAllText(path, "#!/bin/sh\n");

        return path;
    }

    private static IConfiguration Reload(TempDataDirectory dir, Dictionary<string, string> values) =>
        new ConfigurationBuilder().AddConfiguration(dir.Configuration(values)).AddJsonFile(dir.ConfigFile).Build();
}
