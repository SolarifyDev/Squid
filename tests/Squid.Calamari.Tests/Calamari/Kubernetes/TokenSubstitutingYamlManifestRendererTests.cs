using Squid.Calamari.Kubernetes;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Tests.Calamari.Kubernetes;

public class TokenSubstitutingYamlManifestRendererTests
{
    [Fact]
    public async Task RenderAsync_SingleFile_ReplacesTokens_AndWritesExpandedFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-renderer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var yamlPath = Path.Combine(tempDir, "input.yaml");
            File.WriteAllText(yamlPath, "name: #{App}\nns: #{Namespace}\n");

            var variables = new VariableSet();
            variables.Set("App", "squid");
            variables.Set("Namespace", "dev");

            var renderer = new TokenSubstitutingYamlManifestRenderer();
            var trackedTemps = new List<string>();
            var rendered = await renderer.RenderAsync(
                new KubernetesApplyRequest
                {
                    WorkingDirectory = tempDir,
                    YamlFilePath = yamlPath,
                    Variables = variables,
                    Namespace = "ignored",
                    TemporaryFiles = trackedTemps
                },
                new ResolvedKubernetesManifestSource
                {
                    ManifestRootDirectory = tempDir,
                    ManifestFilePaths = [yamlPath]
                },
                CancellationToken.None);

            rendered.Recursive.ShouldBeFalse();
            rendered.ApplyPath.ShouldStartWith(Path.Combine(tempDir, ".squid-expanded-"));
            rendered.ApplyPath.ShouldEndWith("-input.yaml");
            File.ReadAllText(rendered.ApplyPath).ShouldBe("name: squid\nns: dev\n");
            trackedTemps.ShouldContain(rendered.ApplyPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAsync_MultiFile_RendersManifestSetDirectory_PreservingRelativePaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-renderer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var sourceDir = Path.Combine(tempDir, "source");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(Path.Combine(sourceDir, "nested"));

            var fileA = Path.Combine(sourceDir, "a.yaml");
            var fileB = Path.Combine(sourceDir, "nested", "b.yml");
            File.WriteAllText(fileA, "name: #{App}\n");
            File.WriteAllText(fileB, "ns: #{Namespace}\n");

            var variables = new VariableSet();
            variables.Set("App", "squid");
            variables.Set("Namespace", "prod");

            var trackedTemps = new List<string>();
            var renderer = new TokenSubstitutingYamlManifestRenderer();

            var rendered = await renderer.RenderAsync(
                new KubernetesApplyRequest
                {
                    WorkingDirectory = tempDir,
                    YamlFilePath = sourceDir,
                    Variables = variables,
                    TemporaryFiles = trackedTemps
                },
                new ResolvedKubernetesManifestSource
                {
                    ManifestRootDirectory = sourceDir,
                    ManifestFilePaths = [fileA, fileB]
                },
                CancellationToken.None);

            rendered.Recursive.ShouldBeTrue();
            Directory.Exists(rendered.ApplyPath).ShouldBeTrue();
            rendered.ApplyPath.ShouldStartWith(Path.Combine(tempDir, ".squid-expanded-manifests-"));
            trackedTemps.ShouldContain(rendered.ApplyPath);

            File.ReadAllText(Path.Combine(rendered.ApplyPath, "a.yaml")).ShouldBe("name: squid\n");
            File.ReadAllText(Path.Combine(rendered.ApplyPath, "nested", "b.yml")).ShouldBe("ns: prod\n");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
