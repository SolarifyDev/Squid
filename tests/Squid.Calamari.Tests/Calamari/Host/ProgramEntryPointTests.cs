using Squid.Calamari.Tests.TestSupport;

namespace Squid.Calamari.Tests.Calamari.Host;

[Collection("Console IO")]
public class ProgramEntryPointTests
{
    [Fact]
    public async Task NoArgs_Returns1_AndPrintsUsage()
    {
        var result = await CalamariTestHost.InvokeInProcessAsync();

        result.ExitCode.ShouldBe(1);
        result.Stdout.ShouldContain("squid-calamari <subcommand> [options]");
        result.Stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnknownSubcommand_Returns1_AndPrintsError()
    {
        var result = await CalamariTestHost.InvokeInProcessAsync("nope");

        result.ExitCode.ShouldBe(1);
        result.Stderr.ShouldContain("Unknown subcommand: nope");
        result.Stdout.ShouldContain("Subcommands:");
    }

    [Fact]
    public async Task RunScript_WithoutScriptArg_Returns1()
    {
        var result = await CalamariTestHost.InvokeInProcessAsync("run-script");

        result.ExitCode.ShouldBe(1);
        result.Stderr.ShouldContain("run-script requires --script=<path>");
    }

    [Fact]
    public async Task ApplyYaml_WithoutFileArg_Returns1()
    {
        var result = await CalamariTestHost.InvokeInProcessAsync("apply-yaml");

        result.ExitCode.ShouldBe(1);
        result.Stderr.ShouldContain("apply-yaml requires --file=<path>");
    }

    [Fact]
    public async Task RunScript_HappyPath_Returns0_AndSuppressesServiceMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptPath = Path.Combine(tempDir, "hello.sh");
            File.WriteAllText(scriptPath,
                "echo hello-from-inprocess\n" +
                "echo \"##squid[setVariable name='BuildId' value='42']\"\n");

            var result = await CalamariTestHost.InvokeInProcessAsync("run-script", $"--script={scriptPath}");

            result.ExitCode.ShouldBe(0);
            result.Stdout.ShouldContain("hello-from-inprocess");
            result.Stdout.ShouldNotContain("##squid[setVariable");
            result.Stderr.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunScript_FailureExitCode_IsReturnedByProgram()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptPath = Path.Combine(tempDir, "fail.sh");
            File.WriteAllText(scriptPath, "exit 7\n");

            var result = await CalamariTestHost.InvokeInProcessAsync("run-script", $"--script={scriptPath}");

            result.ExitCode.ShouldBe(7);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
