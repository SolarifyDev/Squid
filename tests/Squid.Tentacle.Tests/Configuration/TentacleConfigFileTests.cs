using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Configuration;

namespace Squid.Tentacle.Tests.Configuration;

public class TentacleConfigFileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public TentacleConfigFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "instance.config.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var file = new TentacleConfigFile(_configPath);

        file.Exists().ShouldBeFalse();
        file.Load().ShouldBeEmpty();
    }

    [Fact]
    public void Save_WritesNestedJson_WithColonSeparatedKeysUnpacked()
    {
        var file = new TentacleConfigFile(_configPath);

        file.Save(new Dictionary<string, string>
        {
            ["Tentacle:Flavor"] = "LinuxTentacle",
            ["Tentacle:ServerUrl"] = "https://squid:7078",
            ["Tentacle:ListeningPort"] = "10933"
        });

        var json = File.ReadAllText(_configPath);
        var root = JsonNode.Parse(json).AsObject();

        root["Tentacle"].AsObject()["Flavor"].ToString().ShouldBe("LinuxTentacle");
        root["Tentacle"].AsObject()["ServerUrl"].ToString().ShouldBe("https://squid:7078");
        root["Tentacle"].AsObject()["ListeningPort"].ToString().ShouldBe("10933");
    }

    [Fact]
    public void Load_ReadsNestedJsonBackAsFlatColonKeys()
    {
        File.WriteAllText(_configPath, """
        {
          "Tentacle": {
            "Flavor": "LinuxTentacle",
            "Server": { "Url": "https://squid:7078" }
          }
        }
        """);

        var loaded = new TentacleConfigFile(_configPath).Load();

        loaded["Tentacle:Flavor"].ShouldBe("LinuxTentacle");
        loaded["Tentacle:Server:Url"].ShouldBe("https://squid:7078");
    }

    [Fact]
    public void Merge_PreservesExistingKeys_NotInTheUpdateSet()
    {
        var file = new TentacleConfigFile(_configPath);
        file.Save(new Dictionary<string, string> { ["Tentacle:Flavor"] = "LinuxTentacle", ["Tentacle:Roles"] = "web" });

        file.Merge(new Dictionary<string, string> { ["Tentacle:ServerUrl"] = "https://new" });

        var loaded = file.Load();
        loaded["Tentacle:Flavor"].ShouldBe("LinuxTentacle");
        loaded["Tentacle:Roles"].ShouldBe("web");
        loaded["Tentacle:ServerUrl"].ShouldBe("https://new");
    }

    [Fact]
    public void Merge_EmptyValues_SkippedSoTheyDoNotClobberExistingEntries()
    {
        var file = new TentacleConfigFile(_configPath);
        file.Save(new Dictionary<string, string> { ["Tentacle:MachineName"] = "web-01" });

        file.Merge(new Dictionary<string, string>
        {
            ["Tentacle:MachineName"] = "",                // empty — must not overwrite
            ["Tentacle:ServerUrl"] = "https://ok"
        });

        var loaded = file.Load();
        loaded["Tentacle:MachineName"].ShouldBe("web-01");
        loaded["Tentacle:ServerUrl"].ShouldBe("https://ok");
    }

    [Fact]
    public void Remove_DropsSingleKey_RewritesFile()
    {
        var file = new TentacleConfigFile(_configPath);
        file.Save(new Dictionary<string, string>
        {
            ["Tentacle:Flavor"] = "LinuxTentacle",
            ["Tentacle:ApiKey"] = "SECRET"
        });

        file.Remove("Tentacle:ApiKey");

        var loaded = file.Load();
        loaded.ShouldContainKey("Tentacle:Flavor");
        loaded.ShouldNotContainKey("Tentacle:ApiKey");
    }

    [Fact]
    public void ProducedFile_IsConsumableByIConfigurationBuilder()
    {
        // Integration check: the output of TentacleConfigFile must be loadable via
        // IConfigurationBuilder.AddJsonFile — that's the contract Program.cs depends on.
        var file = new TentacleConfigFile(_configPath);
        file.Save(new Dictionary<string, string>
        {
            ["Tentacle:Flavor"] = "LinuxTentacle",
            ["Tentacle:ListeningPort"] = "10933"
        });

        var config = new ConfigurationBuilder()
            .AddJsonFile(_configPath, optional: false)
            .Build();

        config["Tentacle:Flavor"].ShouldBe("LinuxTentacle");
        config["Tentacle:ListeningPort"].ShouldBe("10933");
    }
}
