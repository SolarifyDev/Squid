using System.Linq;
using Squid.Core.Services.DeploymentExecution.Script.Files;

namespace Squid.UnitTests.Services.Deployments.Execution.Files;

public class DeploymentFileCollectionTests
{
    private static readonly byte[] SampleContent = { 0x01, 0x02, 0x03 };

    [Fact]
    public void Empty_HasZeroCount()
    {
        DeploymentFileCollection.Empty.Count.ShouldBe(0);
        DeploymentFileCollection.Empty.Any().ShouldBeFalse();
        DeploymentFileCollection.Empty.HasNestedPaths().ShouldBeFalse();
    }

    [Fact]
    public void Constructor_WithValidFiles_ExposesAllEntries()
    {
        var files = new[]
        {
            DeploymentFile.Asset("deploy.yaml", SampleContent),
            DeploymentFile.Asset("content/values.yaml", SampleContent)
        };

        var collection = new DeploymentFileCollection(files);

        collection.Count.ShouldBe(2);
        collection[0].RelativePath.ShouldBe("deploy.yaml");
        collection[1].RelativePath.ShouldBe("content/values.yaml");
    }

    [Fact]
    public void Constructor_NullInput_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new DeploymentFileCollection(null!));
    }

    [Fact]
    public void Constructor_DuplicatePath_Throws()
    {
        var files = new[]
        {
            DeploymentFile.Asset("deploy.yaml", SampleContent),
            DeploymentFile.Asset("deploy.yaml", SampleContent)
        };

        var ex = Should.Throw<ArgumentException>(() => new DeploymentFileCollection(files));
        ex.Message.ShouldContain("duplicate");
    }

    [Fact]
    public void Constructor_DuplicatePathCaseInsensitive_Throws()
    {
        var files = new[]
        {
            DeploymentFile.Asset("Deploy.yaml", SampleContent),
            DeploymentFile.Asset("deploy.yaml", SampleContent)
        };

        Should.Throw<ArgumentException>(() => new DeploymentFileCollection(files));
    }

    [Fact]
    public void Constructor_InvalidFile_ThrowsFromEnsureValid()
    {
        var files = new[]
        {
            DeploymentFile.Asset("../etc/passwd", SampleContent)
        };

        Should.Throw<ArgumentException>(() => new DeploymentFileCollection(files));
    }

    [Fact]
    public void Constructor_NullEntry_Throws()
    {
        var files = new DeploymentFile[] { null! };

        Should.Throw<ArgumentException>(() => new DeploymentFileCollection(files));
    }

    [Fact]
    public void HasNestedPaths_DetectsNestedEntries()
    {
        var flat = new DeploymentFileCollection(new[]
        {
            DeploymentFile.Asset("a.yaml", SampleContent),
            DeploymentFile.Asset("b.yaml", SampleContent)
        });

        var nested = new DeploymentFileCollection(new[]
        {
            DeploymentFile.Asset("a.yaml", SampleContent),
            DeploymentFile.Asset("content/b.yaml", SampleContent)
        });

        flat.HasNestedPaths().ShouldBeFalse();
        nested.HasNestedPaths().ShouldBeTrue();
    }

    [Fact]
    public void Enumeration_YieldsAllEntriesInOrder()
    {
        var files = new[]
        {
            DeploymentFile.Asset("a.yaml", SampleContent),
            DeploymentFile.Asset("b.yaml", SampleContent),
            DeploymentFile.Asset("c.yaml", SampleContent)
        };

        var collection = new DeploymentFileCollection(files);
        var paths = collection.Select(f => f.RelativePath).ToList();

        paths.ShouldBe(new[] { "a.yaml", "b.yaml", "c.yaml" });
    }

    // ========== FromLegacyFiles ==========

    [Fact]
    public void FromLegacyFiles_Null_ReturnsEmpty()
    {
        var collection = DeploymentFileCollection.FromLegacyFiles(null);

        collection.ShouldBeSameAs(DeploymentFileCollection.Empty);
    }

    [Fact]
    public void FromLegacyFiles_Empty_ReturnsEmpty()
    {
        var collection = DeploymentFileCollection.FromLegacyFiles(new Dictionary<string, byte[]>());

        collection.ShouldBeSameAs(DeploymentFileCollection.Empty);
    }

    [Fact]
    public void FromLegacyFiles_PopulatedDictionary_ConvertsEntriesToAssetFiles()
    {
        var legacy = new Dictionary<string, byte[]>
        {
            ["deploy.yaml"] = SampleContent,
            ["content/values.yaml"] = SampleContent
        };

        var collection = DeploymentFileCollection.FromLegacyFiles(legacy);

        collection.Count.ShouldBe(2);
        collection.All(f => f.Kind == DeploymentFileKind.Asset).ShouldBeTrue();
        collection.Select(f => f.RelativePath).ShouldBe(new[] { "deploy.yaml", "content/values.yaml" }, ignoreOrder: true);
    }

    [Fact]
    public void FromLegacyFiles_NestedPaths_HasNestedPathsTrue()
    {
        var legacy = new Dictionary<string, byte[]>
        {
            ["content/values.yaml"] = SampleContent
        };

        var collection = DeploymentFileCollection.FromLegacyFiles(legacy);

        collection.HasNestedPaths().ShouldBeTrue();
    }
}
