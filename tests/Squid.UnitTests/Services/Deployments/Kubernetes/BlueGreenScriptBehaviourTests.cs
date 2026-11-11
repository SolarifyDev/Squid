using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Executes the generated <c>bluegreen.sh</c> under real bash with a stub <c>kubectl</c> on
/// PATH. The stub records every invocation and answers the Service / rollout probes from
/// environment variables, so the runtime slot-derivation and switch ordering are proven
/// without a cluster. Real-cluster proof lives in <c>KubernetesBlueGreenRealClusterE2ETests</c>.
/// </summary>
public class BlueGreenScriptBehaviourTests
{
    private const string StubKubectl = """
        #!/bin/bash
        echo "$*" >> "$STUB_LOG"
        case "$1 $2" in
          "get service")
            if [ "$STUB_SERVICE_EXISTS" != "true" ]; then exit 1; fi
            case "$*" in *jsonpath*) printf '%s' "$STUB_ACTIVE_SLOT" ;; esac
            exit 0 ;;
          "get deployment") [ "$STUB_OLD_EXISTS" = "true" ] && exit 0 || exit 1 ;;
          "apply -f") cat >> "$STUB_APPLIED"; exit 0 ;;
          "rollout status") exit "${STUB_ROLLOUT_EXIT:-0}" ;;
        esac
        exit 0
        """;

    [Theory]
    [InlineData("", "", "green", "blue")]
    [InlineData("blue", "", "green", "blue")]
    [InlineData("green", "", "blue", "green")]
    [InlineData("", "green", "blue", "green")]
    [InlineData("blue", "green", "green", "blue")]
    public async Task Script_ServiceExists_DeploysOtherSlot_SwitchesThenScalesDownOld(string liveSlot, string seedSlot, string expectedNew, string expectedOld)
    {
        if (OperatingSystem.IsWindows()) return;

        using var run = new StubRun(serviceExists: true, activeSlot: liveSlot, oldExists: true);

        var exitCode = await run.ExecuteAsync(await GenerateScriptAsync(seedSlot));

        exitCode.ShouldBe(0, run.Diagnostics());
        run.Applied.ShouldContain($"name: \"web-{expectedNew}\"");
        run.Applied.ShouldNotContain($"name: \"web-{expectedOld}\"");

        var calls = run.Calls;
        var rollout = calls.FindIndex(c => c.StartsWith($"rollout status deployment/web-{expectedNew}", StringComparison.Ordinal));
        var patch = calls.FindIndex(c => c.StartsWith("patch service web-svc", StringComparison.Ordinal) && c.Contains($"\"value\": \"{expectedNew}\""));
        var scale = calls.FindIndex(c => c.StartsWith($"scale deployment web-{expectedOld}", StringComparison.Ordinal) && c.EndsWith("--replicas=0", StringComparison.Ordinal));

        rollout.ShouldBeGreaterThan(-1, run.Diagnostics());
        patch.ShouldBeGreaterThan(rollout, run.Diagnostics());
        scale.ShouldBeGreaterThan(patch, run.Diagnostics());
    }

    [Fact]
    public async Task Script_RolloutFails_ExitsNonZero_TrafficUntouched()
    {
        if (OperatingSystem.IsWindows()) return;

        using var run = new StubRun(serviceExists: true, activeSlot: "blue", oldExists: true, rolloutExit: 1);

        var exitCode = await run.ExecuteAsync(await GenerateScriptAsync(seedSlot: ""));

        exitCode.ShouldNotBe(0, run.Diagnostics());
        run.Calls.ShouldNotContain(c => c.StartsWith("patch", StringComparison.Ordinal));
        run.Calls.ShouldNotContain(c => c.StartsWith("scale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Script_NoService_AppliesGreen_SkipsSwitchAndScaleDown()
    {
        if (OperatingSystem.IsWindows()) return;

        using var run = new StubRun(serviceExists: false, activeSlot: "", oldExists: true);

        var exitCode = await run.ExecuteAsync(await GenerateScriptAsync(seedSlot: ""));

        exitCode.ShouldBe(0, run.Diagnostics());
        run.Applied.ShouldContain("name: \"web-green\"");
        run.Calls.ShouldNotContain(c => c.StartsWith("patch", StringComparison.Ordinal));
        run.Calls.ShouldNotContain(c => c.StartsWith("scale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Script_OldSlotMissing_SwitchesWithoutScaling()
    {
        if (OperatingSystem.IsWindows()) return;

        using var run = new StubRun(serviceExists: true, activeSlot: "", oldExists: false);

        var exitCode = await run.ExecuteAsync(await GenerateScriptAsync(seedSlot: ""));

        exitCode.ShouldBe(0, run.Diagnostics());
        run.Calls.ShouldContain(c => c.StartsWith("patch service web-svc", StringComparison.Ordinal));
        run.Calls.ShouldNotContain(c => c.StartsWith("scale", StringComparison.Ordinal));
    }

    private static async Task<string> GenerateScriptAsync(string seedSlot)
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers", Name = "web" };

        Add(action, "Squid.Action.KubernetesContainers.DeploymentName", "web");
        Add(action, "Squid.Action.KubernetesContainers.Namespace", "bg-ns");
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen");
        Add(action, "Squid.Action.KubernetesContainers.ServiceName", "web-svc");
        Add(action, "Squid.Action.KubernetesContainers.BlueGreenActiveSlot", seedSlot);
        Add(action, "Squid.Action.KubernetesContainers.Containers", """[{"Name":"app","Image":"busybox:latest"}]""");

        var files = await new KubernetesContainersActionYamlGenerator().GenerateAsync(step, action, CancellationToken.None);

        return Encoding.UTF8.GetString(files[BlueGreenResourceGenerator.ScriptFileName]);
    }

    private static void Add(DeploymentActionDto action, string name, string value)
        => action.Properties.Add(new DeploymentActionPropertyDto { PropertyName = name, PropertyValue = value });

    private sealed class StubRun : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"squid-bg-stub-{Guid.NewGuid():N}");
        private readonly Dictionary<string, string> _env;
        private string _stdout = string.Empty;
        private string _stderr = string.Empty;

        public StubRun(bool serviceExists, string activeSlot, bool oldExists, int rolloutExit = 0)
        {
            Directory.CreateDirectory(_dir);

            var kubectl = Path.Combine(_dir, "kubectl");
            File.WriteAllText(kubectl, StubKubectl.Replace("\r\n", "\n"));
            File.SetUnixFileMode(kubectl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            _env = new Dictionary<string, string>
            {
                ["PATH"] = $"{_dir}:{Environment.GetEnvironmentVariable("PATH")}",
                ["STUB_LOG"] = Path.Combine(_dir, "calls.log"),
                ["STUB_APPLIED"] = Path.Combine(_dir, "applied.yaml"),
                ["STUB_SERVICE_EXISTS"] = serviceExists ? "true" : "false",
                ["STUB_ACTIVE_SLOT"] = activeSlot,
                ["STUB_OLD_EXISTS"] = oldExists ? "true" : "false",
                ["STUB_ROLLOUT_EXIT"] = rolloutExit.ToString()
            };
        }

        public List<string> Calls => File.Exists(_env["STUB_LOG"]) ? File.ReadAllLines(_env["STUB_LOG"]).ToList() : new List<string>();

        public string Applied => File.Exists(_env["STUB_APPLIED"]) ? File.ReadAllText(_env["STUB_APPLIED"]) : string.Empty;

        public async Task<int> ExecuteAsync(string script)
        {
            var scriptPath = Path.Combine(_dir, "bluegreen.sh");
            await File.WriteAllTextAsync(scriptPath, script);

            var startInfo = new ProcessStartInfo("bash", scriptPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

            foreach (var (key, value) in _env)
                startInfo.Environment[key] = value;

            using var process = System.Diagnostics.Process.Start(startInfo)!;
            _stdout = await process.StandardOutput.ReadToEndAsync();
            _stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return process.ExitCode;
        }

        public string Diagnostics() => $"kubectl calls:\n{string.Join("\n", Calls)}\n\nstdout:\n{_stdout}\n\nstderr:\n{_stderr}";

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
