using Squid.Calamari.Tests.TestSupport;

namespace Squid.Calamari.Tests.Calamari.Cli;

[Collection("Process Environment")]
public class CliSmokeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NoArgs_Returns1_AndPrintsUsage()
    {
        var result = await CalamariTestHost.InvokeCliAsync();

        result.ExitCode.ShouldBe(1);
        result.Stdout.ShouldContain("squid-calamari <subcommand> [options]");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunScript_HappyPath_Returns0_AndPrintsScriptOutput()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-cli-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptPath = Path.Combine(tempDir, "smoke.sh");
            File.WriteAllText(scriptPath,
                "echo hello-from-cli\n" +
                "echo \"##squid[setVariable name='X' value='1']\"\n");

            var result = await CalamariTestHost.InvokeCliAsync(tempDir, "run-script", $"--script={scriptPath}");

            result.ExitCode.ShouldBe(0);
            result.Stdout.ShouldContain("hello-from-cli");
            result.Stdout.ShouldNotContain("##squid[setVariable");
            result.Stderr.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

}
