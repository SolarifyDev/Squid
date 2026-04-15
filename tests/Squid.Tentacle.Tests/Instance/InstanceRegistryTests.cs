using System.IO;
using System.Linq;
using Squid.Tentacle.Instance;

namespace Squid.Tentacle.Tests.Instance;

public class InstanceRegistryTests : IDisposable
{
    private readonly string _tempDir;

    public InstanceRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"squid-reg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void List_EmptyRegistry_ReturnsEmpty()
    {
        new InstanceRegistry(_tempDir).List().ShouldBeEmpty();
    }

    [Fact]
    public void Add_ThenList_ReturnsTheInstance()
    {
        var registry = new InstanceRegistry(_tempDir);

        registry.Add(new InstanceRecord { Name = "production", ConfigPath = "/etc/foo.json" });

        var listed = registry.List();
        listed.Count.ShouldBe(1);
        listed[0].Name.ShouldBe("production");
        listed[0].ConfigPath.ShouldBe("/etc/foo.json");
    }

    [Fact]
    public void Add_DuplicateName_Throws()
    {
        var registry = new InstanceRegistry(_tempDir);
        registry.Add(new InstanceRecord { Name = "prod", ConfigPath = "/etc/prod.json" });

        Should.Throw<InvalidOperationException>(() =>
            registry.Add(new InstanceRecord { Name = "prod", ConfigPath = "/etc/other.json" }));
    }

    [Fact]
    public void Find_CaseInsensitive_MatchesAcrossCases()
    {
        var registry = new InstanceRegistry(_tempDir);
        registry.Add(new InstanceRecord { Name = "Production", ConfigPath = "/etc/x.json" });

        registry.Find("production").ShouldNotBeNull();
        registry.Find("PRODUCTION").ShouldNotBeNull();
        registry.Find("Production").ShouldNotBeNull();
        registry.Find("staging").ShouldBeNull();
    }

    [Fact]
    public void Remove_DropsRecord_AndPersists()
    {
        var registry = new InstanceRegistry(_tempDir);
        registry.Add(new InstanceRecord { Name = "prod", ConfigPath = "/etc/prod.json" });
        registry.Add(new InstanceRecord { Name = "staging", ConfigPath = "/etc/stg.json" });

        registry.Remove("prod");

        var fresh = new InstanceRegistry(_tempDir);   // re-read from disk
        fresh.List().Select(i => i.Name).ShouldBe(["staging"]);
    }

    [Fact]
    public void EnsureDefault_CreatesTheDefaultInstance_IfMissing()
    {
        var registry = new InstanceRegistry(_tempDir);

        var record = registry.EnsureDefault();

        record.Name.ShouldBe("Default");
        registry.Find("Default").ShouldNotBeNull();
    }

    [Fact]
    public void EnsureDefault_IsIdempotent()
    {
        var registry = new InstanceRegistry(_tempDir);
        registry.EnsureDefault();
        registry.EnsureDefault();      // second call must not throw

        registry.List().Count.ShouldBe(1);
    }

    [Fact]
    public void FindOrDefault_AbsentName_ReturnsSynthesizedRecord_WithoutPersisting()
    {
        var registry = new InstanceRegistry(_tempDir);

        var synthesized = registry.FindOrDefault("nonexistent");

        synthesized.Name.ShouldBe("nonexistent");
        synthesized.ConfigPath.ShouldEndWith("nonexistent.config.json");
        registry.List().ShouldBeEmpty("FindOrDefault must not write to disk");
    }

    [Fact]
    public void Add_PersistsAcrossInstances_ViaOnDiskFile()
    {
        new InstanceRegistry(_tempDir).Add(new InstanceRecord { Name = "a", ConfigPath = "/etc/a.json" });
        new InstanceRegistry(_tempDir).Add(new InstanceRecord { Name = "b", ConfigPath = "/etc/b.json" });

        var reloaded = new InstanceRegistry(_tempDir).List();
        reloaded.Count.ShouldBe(2);
        reloaded.Select(i => i.Name).ShouldContain("a");
        reloaded.Select(i => i.Name).ShouldContain("b");
    }
}
