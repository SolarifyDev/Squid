using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesPostApplyScriptBuilderTests
{
    [Fact]
    public void Build_NoScripts_ReturnsEmpty()
    {
        KubernetesPostApplyScriptBuilder.Build(NewIntent()).ShouldBe(string.Empty);
    }

    [Fact]
    public void Build_MultipleScripts_EmittedInPathOrder()
    {
        var script = KubernetesPostApplyScriptBuilder.Build(NewIntent(("b.sh", "echo B"), ("a.sh", "echo A")));

        script.IndexOf("echo A", StringComparison.Ordinal).ShouldBeLessThan(script.IndexOf("echo B", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_NonBashSyntax_Throws()
    {
        var intent = NewIntent(("bluegreen.sh", "echo hi")) with { Syntax = ScriptSyntax.PowerShell };

        Should.Throw<NotSupportedException>(() => KubernetesPostApplyScriptBuilder.Build(intent));
    }

    [Theory]
    [InlineData("exit 0", 0, true)]
    [InlineData("exit 7", 7, false)]
    [InlineData("set -e; false; echo never", 1, false)]
    public async Task Build_ScriptRunsIsolated_ExitCodePropagates(string body, int expectedExit, bool trailerRuns)
    {
        if (OperatingSystem.IsWindows()) return;

        // A trailer after the block stands in for whatever a renderer wrapper might append:
        // a script's own `exit 0` / `set -e` must not leak out, but a failure must stop the run.
        var rendered = KubernetesPostApplyScriptBuilder.Build(NewIntent(("bluegreen.sh", body))) + "echo TRAILER\n";

        var (exitCode, stdout) = await RunBashAsync(rendered);

        exitCode.ShouldBe(expectedExit);
        stdout.Contains("TRAILER").ShouldBe(trailerRuns);
    }

    [Fact]
    public void Compose_NoScripts_ReturnsApplyScriptUnchanged()
    {
        KubernetesPostApplyScriptBuilder.Compose(NewIntent(), "kubectl apply -f \"./a.yaml\"\n").ShouldBe("kubectl apply -f \"./a.yaml\"\n");
    }

    [Theory]
    [InlineData("true\n", 0, true)]
    [InlineData("false\necho LATER_APPLY\n", 1, false)]
    public async Task Compose_WithScripts_EarlierFailureStopsBeforePostApply(string applyScript, int expectedExit, bool postApplyRuns)
    {
        if (OperatingSystem.IsWindows()) return;

        var composed = KubernetesPostApplyScriptBuilder.Compose(NewIntent(("bluegreen.sh", "echo POST_APPLY")), applyScript);

        var (exitCode, stdout) = await RunBashAsync(composed);

        exitCode.ShouldBe(expectedExit);
        stdout.Contains("POST_APPLY").ShouldBe(postApplyRuns);
        stdout.Contains("LATER_APPLY").ShouldBeFalse();
    }

    private static KubernetesApplyIntent NewIntent(params (string Path, string Body)[] scripts) => new()
    {
        Name = "k8s-apply",
        YamlFiles = Array.Empty<DeploymentFile>(),
        PostApplyScripts = scripts.Select(s => DeploymentFile.Script(s.Path, Encoding.UTF8.GetBytes(s.Body))).ToList()
    };

    private static async Task<(int ExitCode, string StdOut)> RunBashAsync(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squid-postapply-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(path, script);

        try
        {
            using var process = System.Diagnostics.Process.Start(new ProcessStartInfo("bash", path) { RedirectStandardOutput = true, UseShellExecute = false })!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return (process.ExitCode, stdout);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
