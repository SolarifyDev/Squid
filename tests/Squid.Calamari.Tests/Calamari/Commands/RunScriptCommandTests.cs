using Squid.Calamari.Commands;

namespace Squid.Calamari.Tests.Calamari.Commands;

[Collection("Process Environment")]
public class RunScriptCommandTests
{
    [Fact]
    public async Task ExecuteWithResultAsync_ReturnsExitCode_And_OutputVariables()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var scriptPath = Path.Combine(tempDir, "command.sh");
            File.WriteAllText(scriptPath,
                "echo hello-from-command\n" +
                "echo \"##squid[setVariable name='Result' value='OK']\"\n" +
                "exit 0\n");

            var command = new RunScriptCommand();
            var result = await command.ExecuteWithResultAsync(
                scriptPath,
                Path.Combine(tempDir, "variables.json"),
                null,
                null,
                CancellationToken.None);

            result.ExitCode.ShouldBe(0);
            result.Succeeded.ShouldBeTrue();
            result.OutputVariables.Count.ShouldBe(1);
            result.OutputVariables[0].Name.ShouldBe("Result");
            result.OutputVariables[0].Value.ShouldBe("OK");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
