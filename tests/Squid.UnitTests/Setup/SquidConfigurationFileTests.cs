using System.IO;
using Microsoft.Extensions.Configuration;
using Squid.Core.Setup.Configuration;

namespace Squid.UnitTests.Setup;

public sealed class SquidConfigurationFileTests
{
    [Fact]
    public void Merge_WritesNestedKeys_ReadableByConfiguration()
    {
        using var dir = new TempDataDirectory();

        SquidConfigurationFile.Merge(dir.ConfigFile, new Dictionary<string, string> { ["SquidStore:ConnectionString"] = "Host=a", ["RedisCacheConnectionString"] = "r:1" });

        var configuration = new ConfigurationBuilder().AddJsonFile(dir.ConfigFile).Build();

        configuration["SquidStore:ConnectionString"].ShouldBe("Host=a");
        configuration["RedisCacheConnectionString"].ShouldBe("r:1");
    }

    [Fact]
    public void Merge_KeepsExistingValues_AndOverwritesOnlyGivenKeys()
    {
        using var dir = new TempDataDirectory();

        SquidConfigurationFile.Merge(dir.ConfigFile, new Dictionary<string, string> { ["A:B"] = "1", ["A:C"] = "2" });
        SquidConfigurationFile.Merge(dir.ConfigFile, new Dictionary<string, string> { ["A:C"] = "3", ["D"] = "4" });

        SquidConfigurationFile.GetValue(dir.ConfigFile, "A:B").ShouldBe("1");
        SquidConfigurationFile.GetValue(dir.ConfigFile, "A:C").ShouldBe("3");
        SquidConfigurationFile.GetValue(dir.ConfigFile, "D").ShouldBe("4");
    }

    [Fact]
    public void Merge_DoesNotEscapeSecretsIntoUnreadableJson()
    {
        using var dir = new TempDataDirectory();

        SquidConfigurationFile.Merge(dir.ConfigFile, new Dictionary<string, string> { ["K"] = "a+b/c=" });

        File.ReadAllText(dir.ConfigFile).ShouldContain("a+b/c=");
    }

    [Fact]
    public void Merge_FileIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDataDirectory();

        SquidConfigurationFile.Merge(dir.ConfigFile, new Dictionary<string, string> { ["K"] = "v" });

        File.GetUnixFileMode(dir.ConfigFile).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void Merge_NothingToWrite_DoesNotCreateFile()
    {
        using var dir = new TempDataDirectory();

        SquidConfigurationFile.Merge(dir.ConfigFile, new Dictionary<string, string>());

        File.Exists(dir.ConfigFile).ShouldBeFalse();
    }

    [Fact]
    public void GetValue_MissingFileOrKey_ReturnsNull()
    {
        using var dir = new TempDataDirectory();

        SquidConfigurationFile.GetValue(dir.ConfigFile, "A:B").ShouldBeNull();
    }
}
