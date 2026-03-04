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

            resolved.ManifestRootDirectory.ShouldBe(tempDir);
            resolved.ManifestFilePaths.ShouldBe([Path.GetFullPath(filePath)]);
            resolved.CleanupPaths.ShouldBeEmpty();
            resolved.IsSingleFile.ShouldBeTrue();
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

            resolved.ManifestRootDirectory.ShouldBe(tempDir);
            resolved.ManifestFilePaths.ShouldBe([Path.Combine(tempDir, "one.yaml")]);
        }
        finally
        {
            DeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ResolveAsync_WithDirectory_IncludesNestedYamlFiles()
    {
        var tempDir = CreateTempDir();
        try
        {
            var nested = Path.Combine(tempDir, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(tempDir, "root.yaml"), "a");
            File.WriteAllText(Path.Combine(nested, "child.yml"), "b");
            File.WriteAllText(Path.Combine(nested, "ignore.txt"), "x");

            var resolver = new KubernetesManifestSourceResolver();
            var resolved = await resolver.ResolveAsync(tempDir, CancellationToken.None);

            resolved.ManifestRootDirectory.ShouldBe(tempDir);
            resolved.ManifestFilePaths.ShouldBe([
                Path.Combine(nested, "child.yml"),
                Path.Combine(tempDir, "root.yaml")
            ]);
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

            resolved.ManifestRootDirectory.ShouldBe(tempDir);
            resolved.ManifestFilePaths.ShouldBe([Path.Combine(tempDir, "app.yaml")]);
        }
        finally
        {
            DeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ResolveAsync_WithMultipleYamlInDirectory_ReturnsOrderedManifestSet()
    {
        var tempDir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "b.yml"), "b");
            File.WriteAllText(Path.Combine(tempDir, "a.yaml"), "a");

            var resolver = new KubernetesManifestSourceResolver();
            var resolved = await resolver.ResolveAsync(tempDir, CancellationToken.None);

            resolved.IsSingleFile.ShouldBeFalse();
            resolved.ManifestRootDirectory.ShouldBe(tempDir);
            resolved.ManifestFilePaths.ShouldBe([
                Path.Combine(tempDir, "a.yaml"),
                Path.Combine(tempDir, "b.yml")
            ]);
        }
        finally
        {
            DeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ResolveAsync_WithZipArchive_ExtractsYamlFiles_AndReturnsCleanupPath()
    {
        var tempDir = CreateTempDir();
        try
        {
            var zipPath = Path.Combine(tempDir, "bundle.zip");
            using (var stream = File.Create(zipPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("manifests/app.yaml");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("kind: ConfigMap\n");
                }

                var second = archive.CreateEntry("manifests/svc.yml");
                using (var writer2 = new StreamWriter(second.Open()))
                {
                    writer2.Write("kind: Service\n");
                }
            }

            var resolver = new KubernetesManifestSourceResolver();
            var resolved = await resolver.ResolveAsync(zipPath, CancellationToken.None);

            resolved.IsSingleFile.ShouldBeFalse();
            resolved.ManifestRootDirectory.ShouldContain("squid-calamari-k8s-manifest-");
            resolved.ManifestFilePaths.Count.ShouldBe(2);
            foreach (var manifestPath in resolved.ManifestFilePaths)
                File.Exists(manifestPath).ShouldBeTrue();
            resolved.CleanupPaths.Count.ShouldBe(1);
            Directory.Exists(resolved.CleanupPaths[0]).ShouldBeTrue();

            DeleteDir(resolved.CleanupPaths[0]);
        }
        finally
        {
            DeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task ResolveAsync_WithGlobMultipleYaml_ReturnsOrderedManifestSet()
    {
        var tempDir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "z.yaml"), "z");
            File.WriteAllText(Path.Combine(tempDir, "a.yml"), "a");
            File.WriteAllText(Path.Combine(tempDir, "ignore.txt"), "x");

            var resolver = new KubernetesManifestSourceResolver();
            var resolved = await resolver.ResolveAsync(Path.Combine(tempDir, "*.*"), CancellationToken.None);

            resolved.ManifestFilePaths.ShouldBe([
                Path.Combine(tempDir, "a.yml"),
                Path.Combine(tempDir, "z.yaml")
            ]);
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
