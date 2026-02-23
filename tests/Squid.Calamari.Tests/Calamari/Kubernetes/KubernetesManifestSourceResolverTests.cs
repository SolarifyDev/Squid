using System.IO.Compression;
using Squid.Calamari.Kubernetes;

namespace Squid.Calamari.Tests.Calamari.Kubernetes;

public class KubernetesManifestSourceResolverTests
{
    [Fact]
    public async Task ResolveAsync_WithDirectFile_ReturnsFullPath()
    {
        var tempDir = CreateTempDir();
        try
        {
            var filePath = Path.Combine(tempDir, "one.yaml");
            File.WriteAllText(filePath, "kind: ConfigMap\n");

            var resolver = new KubernetesManifestSourceResolver();
            var resolved = await resolver.ResolveAsync(filePath, CancellationToken.None);

            resolved.ManifestFilePath.ShouldBe(Path.GetFullPath(filePath));
            resolved.CleanupPaths.ShouldBeEmpty();
        }
        finally
        {
            DeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ResolveAsync_WithDirectory_ReturnsOnlyYaml()
    {
        var tempDir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "one.yaml"), "a");
            File.WriteAllText(Path.Combine(tempDir, "ignore.txt"), "x");

            var resolver = new KubernetesManifestSourceResolver();
            var resolved = await resolver.ResolveAsync(tempDir, CancellationToken.None);

            resolved.ManifestFilePath.ShouldBe(Path.Combine(tempDir, "one.yaml"));
        }
        finally
        {
            DeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ResolveAsync_WithGlob_ReturnsOnlyYamlMatch()
    {
        var tempDir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "app.yaml"), "a");

            var resolver = new KubernetesManifestSourceResolver();
            var resolved = await resolver.ResolveAsync(Path.Combine(tempDir, "*.yaml"), CancellationToken.None);

            resolved.ManifestFilePath.ShouldBe(Path.Combine(tempDir, "app.yaml"));
        }
        finally
        {
            DeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ResolveAsync_WithMultipleYamlInDirectory_Throws()
    {
        var tempDir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "a.yaml"), "a");
            File.WriteAllText(Path.Combine(tempDir, "b.yml"), "b");

            var resolver = new KubernetesManifestSourceResolver();

            var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
                resolver.ResolveAsync(tempDir, CancellationToken.None));

            ex.Message.ShouldContain("multiple");
        }
        finally
        {
            DeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ResolveAsync_WithZipArchive_ExtractsSingleYaml_AndReturnsCleanupPath()
    {
        var tempDir = CreateTempDir();
        try
        {
            var zipPath = Path.Combine(tempDir, "bundle.zip");
            using (var stream = File.Create(zipPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("manifests/app.yaml");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("kind: ConfigMap\n");
            }

            var resolver = new KubernetesManifestSourceResolver();
            var resolved = await resolver.ResolveAsync(zipPath, CancellationToken.None);

            File.Exists(resolved.ManifestFilePath).ShouldBeTrue();
            resolved.ManifestFilePath.ShouldEndWith(".yaml");
            resolved.CleanupPaths.Count.ShouldBe(1);
            Directory.Exists(resolved.CleanupPaths[0]).ShouldBeTrue();

            DeleteDir(resolved.CleanupPaths[0]);
        }
        finally
        {
            DeleteDir(tempDir);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "squid-calamari-manifest-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
