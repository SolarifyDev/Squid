using Squid.Calamari.Kubernetes;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Tests.Calamari.Kubernetes;

public class TokenSubstitutingYamlManifestRendererTests
{
    [Fact]
    public async Task RenderToFileAsync_ReplacesTokens_AndWritesExpandedFile()
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
            var outputPath = await renderer.RenderToFileAsync(
                new KubernetesApplyRequest
                {
                    WorkingDirectory = tempDir,
                    YamlFilePath = yamlPath,
                    Variables = variables,
                    Namespace = "ignored",
                    TemporaryFiles = trackedTemps
                },
                CancellationToken.None);

            outputPath.ShouldStartWith(Path.Combine(tempDir, ".squid-expanded-"));
            outputPath.ShouldEndWith("-input.yaml");
            File.ReadAllText(outputPath).ShouldBe("name: squid\nns: dev\n");
            trackedTemps.ShouldContain(outputPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
