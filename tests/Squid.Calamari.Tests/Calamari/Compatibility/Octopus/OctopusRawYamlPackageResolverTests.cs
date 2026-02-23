using System.IO.Compression;
using Squid.Calamari.Compatibility.Octopus;

namespace Squid.Calamari.Tests.Calamari.Compatibility.Octopus;

public class OctopusRawYamlPackageResolverTests
{
    [Fact]
    public async Task ResolveAsync_WithYamlFile_ReturnsSamePath_WithoutCleanup()
    {
        var tempDir = CreateTempDir();
        try
        {
            var yamlPath = Path.Combine(tempDir, "manifest.yaml");
            File.WriteAllText(yamlPath, "kind: ConfigMap\n");

            var resolver = new OctopusRawYamlPackageResolver();
            var resolved = await resolver.ResolveAsync(yamlPath, CancellationToken.None);

            resolved.YamlFilePath.ShouldBe(Path.GetFullPath(yamlPath));
            resolved.CleanupPaths.ShouldBeEmpty();
        }
        finally
        {
            DeleteIfExists(tempDir);
        }
    }

    [Fact]
    public async Task ResolveAsync_WithZipContainingSingleYaml_ExtractsAndReturnsYaml()
    {
        var tempDir = CreateTempDir();
        try
        {
            var packagePath = Path.Combine(tempDir, "pkg.zip");
            CreateZip(
                packagePath,
                ("manifests/app.yaml", "kind: Deployment\n"));

            var resolver = new OctopusRawYamlPackageResolver();
            var resolved = await resolver.ResolveAsync(packagePath, CancellationToken.None);

            File.Exists(resolved.YamlFilePath).ShouldBeTrue();
            resolved.YamlFilePath.ShouldEndWith(".yaml");
            resolved.CleanupPaths.Count.ShouldBe(1);
            Directory.Exists(resolved.CleanupPaths[0]).ShouldBeTrue();

            DeleteIfExists(resolved.CleanupPaths[0]);
        }
        finally
        {
            DeleteIfExists(tempDir);
        }
    }

    [Fact]
    public async Task ResolveAsync_WithZipContainingMultipleYaml_Throws()
    {
        var tempDir = CreateTempDir();
        try
        {
            var packagePath = Path.Combine(tempDir, "pkg.zip");
            CreateZip(
                packagePath,
                ("a.yaml", "kind: ConfigMap\n"),
                ("b.yml", "kind: Secret\n"));

            var resolver = new OctopusRawYamlPackageResolver();

            var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
                resolver.ResolveAsync(packagePath, CancellationToken.None));

            ex.Message.ShouldContain("multiple");
        }
        finally
        {
            DeleteIfExists(tempDir);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "squid-calamari-compat-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    private static void CreateZip(string packagePath, params (string EntryPath, string Content)[] entries)
    {
        using var stream = File.Create(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (entryPath, content) in entries)
        {
            var entry = archive.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }
}
