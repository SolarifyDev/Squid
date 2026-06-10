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

    [Fact]
    public async Task MainScriptExitsNonZero_SkipsPostDeploy_RunsDeployFailed()
    {
        // The core fix: a non-zero main-script exit is a failed deploy. PostDeploy
        // (smoke tests / traffic switch) MUST NOT run against a failed deploy, and
        // the DeployFailed hook MUST run. Real bash, real convention scripts.
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var postDeploySentinel = Path.Combine(tempDir, "postdeploy-ran.txt");
        var deployFailedSentinel = Path.Combine(tempDir, "deployfailed-ran.txt");

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "command.sh"), "echo main-running\nexit 3\n");
            File.WriteAllText(Path.Combine(tempDir, "PostDeploy.sh"), $"echo ran > '{postDeploySentinel}'\n");
            File.WriteAllText(Path.Combine(tempDir, "DeployFailed.sh"), $"echo ran > '{deployFailedSentinel}'\n");

            var result = await new RunScriptCommand().ExecuteWithResultAsync(
                Path.Combine(tempDir, "command.sh"), Path.Combine(tempDir, "variables.json"), null, null, CancellationToken.None);

            result.ExitCode.ShouldBe(3);
            result.Succeeded.ShouldBeFalse();

            File.Exists(postDeploySentinel).ShouldBeFalse(
                customMessage: "PostDeploy ran against a FAILED main script (exit 3). A smoke test / traffic " +
                               "switch must not run on a failed deploy — inspect ConventionScriptStep gating.");
            File.Exists(deployFailedSentinel).ShouldBeTrue(
                customMessage: "DeployFailed did NOT run after a non-zero main-script exit. The failure hook " +
                               "must fire so operators can alert / capture forensics.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MainScriptSucceeds_RunsPostDeploy_SkipsDeployFailed()
    {
        // Regression guard for the happy path: a successful main script still runs
        // PostDeploy and must NOT trigger DeployFailed.
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var postDeploySentinel = Path.Combine(tempDir, "postdeploy-ran.txt");
        var deployFailedSentinel = Path.Combine(tempDir, "deployfailed-ran.txt");

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "command.sh"), "echo main-running\nexit 0\n");
            File.WriteAllText(Path.Combine(tempDir, "PostDeploy.sh"), $"echo ran > '{postDeploySentinel}'\n");
            File.WriteAllText(Path.Combine(tempDir, "DeployFailed.sh"), $"echo ran > '{deployFailedSentinel}'\n");

            var result = await new RunScriptCommand().ExecuteWithResultAsync(
                Path.Combine(tempDir, "command.sh"), Path.Combine(tempDir, "variables.json"), null, null, CancellationToken.None);

            result.ExitCode.ShouldBe(0);
            result.Succeeded.ShouldBeTrue();

            File.Exists(postDeploySentinel).ShouldBeTrue(
                customMessage: "PostDeploy did NOT run after a successful main script — the gate over-skipped.");
            File.Exists(deployFailedSentinel).ShouldBeFalse(
                customMessage: "DeployFailed ran on a SUCCESSFUL deploy — the failure predicate is wrong.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PreDeployThrows_SkipsPostDeploy_RunsDeployFailed()
    {
        // The other failure signal (ExecutionFailed via a thrown convention step): a
        // non-zero PreDeploy aborts the deploy before the main script. PostDeploy must not
        // run; DeployFailed must run. The command re-throws the original PreDeploy failure.
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "squid-calamari-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var postDeploySentinel = Path.Combine(tempDir, "postdeploy-ran.txt");
        var deployFailedSentinel = Path.Combine(tempDir, "deployfailed-ran.txt");

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "command.sh"), "echo main-running\nexit 0\n");
            File.WriteAllText(Path.Combine(tempDir, "PreDeploy.sh"), "echo pre-failing\nexit 4\n");
            File.WriteAllText(Path.Combine(tempDir, "PostDeploy.sh"), $"echo ran > '{postDeploySentinel}'\n");
            File.WriteAllText(Path.Combine(tempDir, "DeployFailed.sh"), $"echo ran > '{deployFailedSentinel}'\n");

            await Should.ThrowAsync<InvalidOperationException>(() => new RunScriptCommand().ExecuteWithResultAsync(
                Path.Combine(tempDir, "command.sh"), Path.Combine(tempDir, "variables.json"), null, null, CancellationToken.None));

            File.Exists(postDeploySentinel).ShouldBeFalse(
                customMessage: "PostDeploy ran after PreDeploy threw — a smoke test must not run when the deploy " +
                               "aborted before the main script.");
            File.Exists(deployFailedSentinel).ShouldBeTrue(
                customMessage: "DeployFailed did NOT run after a PreDeploy exception — the cleanup-phase failure " +
                               "hook must fire on the throw path too, not only on a non-zero main-script exit.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
