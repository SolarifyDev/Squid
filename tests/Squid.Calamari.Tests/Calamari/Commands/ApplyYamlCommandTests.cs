using Squid.Calamari.Commands;

namespace Squid.Calamari.Tests.Calamari.Commands;

[Collection("Process Environment")]
public class ApplyYamlCommandTests
{
    [Fact]
    public async Task ExecuteWithResultAsync_ReturnsStructuredResult_And_CollectsServiceMessagesFromKubectl()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var originalPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            var yamlPath = Path.Combine(tempDir, "deployment.yaml");
            File.WriteAllText(yamlPath, "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test\n");

            var kubectlPath = Path.Combine(tempDir, "kubectl");
            File.WriteAllText(kubectlPath,
                "#!/usr/bin/env bash\n" +
                "echo \"fake kubectl $*\"\n" +
                "echo \"##squid[setVariable name='Applied' value='True']\"\n" +
                "exit 0\n");

            File.SetUnixFileMode(
                kubectlPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            Environment.SetEnvironmentVariable("PATH", tempDir + Path.PathSeparator + originalPath);

            var command = new ApplyYamlCommand();
            var result = await command.ExecuteWithResultAsync(
                yamlPath,
                Path.Combine(tempDir, "variables.json"),
                null,
                null,
                "test-ns",
                CancellationToken.None);

            result.ExitCode.ShouldBe(0);
            result.Succeeded.ShouldBeTrue();
            result.OutputVariables.Count.ShouldBe(1);
            result.OutputVariables[0].Name.ShouldBe("Applied");
            result.OutputVariables[0].Value.ShouldBe("True");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);

            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
