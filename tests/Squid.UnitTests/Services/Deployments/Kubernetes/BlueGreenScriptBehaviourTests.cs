using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Executes the generated <c>bluegreen.sh</c> under real bash with a stub <c>kubectl</c> on
/// PATH. The stub mimics kubectl's <c>--ignore-not-found</c> semantics, answers the Service /
/// Deployment / rollout probes from environment variables and records every apply as
/// <c>applied &lt;Kind&gt; &lt;slot&gt;</c>, so slot derivation, pinning and switch ordering are
/// proven without a cluster. Real-cluster proof lives in <c>KubernetesBlueGreenRealClusterE2ETests</c>.
/// </summary>
public class BlueGreenScriptBehaviourTests
{
    private const string StubKubectl = """
        #!/bin/bash
        echo "$*" >> "$STUB_LOG"
        case "$1 $2" in
          "get service")
            if [ "$STUB_SERVICE_ERROR" = "true" ]; then echo "Error from server (Forbidden)" >&2; exit 1; fi
            if [ "$STUB_SERVICE_EXISTS" != "true" ]; then
              case "$*" in *--ignore-not-found*) exit 0 ;; esac
              echo "Error from server (NotFound)" >&2; exit 1
            fi
            case "$*" in *jsonpath*) printf '%s' "$STUB_ACTIVE_SLOT" ;; *"-o name"*) echo "service/$3" ;; esac
            exit 0 ;;
          "get deployment")
            if [ "$STUB_DEPLOYMENT_ERROR" = "true" ]; then echo "Error from server (Forbidden)" >&2; exit 1; fi
            case " $STUB_EXISTING_DEPLOYMENTS " in
              *" $3 "*) echo "deployment.apps/$3"; exit 0 ;;
            esac
            case "$*" in *--ignore-not-found*) exit 0 ;; esac
            echo "Error from server (NotFound)" >&2; exit 1 ;;
          "apply -f")
            content="$(cat)"
            printf '%s\n---\n' "$content" >> "$STUB_APPLIED"
            kind="$(printf '%s\n' "$content" | sed -n 's/^kind: *//p' | head -n1)"
            slot="$(printf '%s\n' "$content" | grep -o 'deployment-slot"\{0,1\}: *"\{0,1\}[a-z]*' | head -n1 | sed 's/.*: *"\{0,1\}//')"
            echo "applied $kind $slot" >> "$STUB_LOG"
            exit 0 ;;
          "rollout status") exit "${STUB_ROLLOUT_EXIT:-0}" ;;
        esac
        exit 0
        """;

    [Theory]
    [InlineData("", "", "", "green", "")]                                // first deploy: nothing to pin
    [InlineData("blue", "", "web-blue", "green", "blue")]                // live selector wins
    [InlineData("green", "", "web-blue web-green", "blue", "green")]
    [InlineData("", "green", "", "blue", "green")]                       // seed property when selector has no slot
    [InlineData("blue", "green", "web-blue web-green", "green", "blue")] // live selector beats seed
    [InlineData("", "", "web-green", "blue", "green")]                   // upgrade: infer live from the only existing slot
    [InlineData("", "", "web-blue", "green", "blue")]
    [InlineData("", "", "web-blue web-green", "green", "")]              // ambiguous: no inference
    [InlineData("purple", "", "", "green", "")]                          // unknown selector value ignored
    public async Task Script_ServiceExists_PinsLive_DeploysOther_SwitchesThenScalesDownOld(string liveSlot, string seedSlot, string existingDeployments, string expectedNew, string expectedPinned)
    {
        if (OperatingSystem.IsWindows()) return;

        var expectedOld = expectedNew == "green" ? "blue" : "green";
        using var run = new StubRun(serviceExists: true, activeSlot: liveSlot, existingDeployments: existingDeployments);

        var exitCode = await run.ExecuteAsync(await GenerateScriptAsync(seedSlot));

        exitCode.ShouldBe(0, run.Diagnostics());

        var expected = new List<string>();
        if (expectedPinned != "") expected.Add($"applied Service {expectedPinned}");
        expected.Add($"applied Deployment {expectedNew}");
        expected.Add($"rollout status deployment/web-{expectedNew}");
        expected.Add($"applied Service {expectedNew}");
        if (existingDeployments.Split(' ').Contains($"web-{expectedOld}")) expected.Add($"scale deployment web-{expectedOld}");

        run.Steps.ShouldBe(expected, ignoreOrder: false, customMessage: run.Diagnostics());
    }

    [Fact]
    public async Task Script_RolloutFails_ExitsNonZero_TrafficStaysOnLiveSlot()
    {
        if (OperatingSystem.IsWindows()) return;

        using var run = new StubRun(serviceExists: true, activeSlot: "blue", existingDeployments: "web-blue web-green", rolloutExit: 1);

        var exitCode = await run.ExecuteAsync(await GenerateScriptAsync(seedSlot: ""));

        exitCode.ShouldNotBe(0, run.Diagnostics());
        run.Steps.ShouldBe(new[] { "applied Service blue", "applied Deployment green", "rollout status deployment/web-green" }, ignoreOrder: false, customMessage: run.Diagnostics());
    }

    [Fact]
    public async Task Script_ServiceMissing_DeploysGreen_CreatesServiceOnSwitch()
    {
        if (OperatingSystem.IsWindows()) return;

        using var run = new StubRun(serviceExists: false, activeSlot: "", existingDeployments: "");

        var exitCode = await run.ExecuteAsync(await GenerateScriptAsync(seedSlot: ""));

        exitCode.ShouldBe(0, run.Diagnostics());
        run.Steps.ShouldBe(new[] { "applied Deployment green", "rollout status deployment/web-green", "applied Service green" }, ignoreOrder: false, customMessage: run.Diagnostics());
    }

    [Fact]
    public async Task Script_ServiceLookupErrors_ExitsNonZero_NothingApplied()
    {
        if (OperatingSystem.IsWindows()) return;

        // An API / RBAC error is not "Service missing": treating it as missing would roll the
        // default slot in place — possibly the live one — and report success.
        using var run = new StubRun(serviceExists: true, activeSlot: "green", existingDeployments: "web-green", serviceError: true);

        var exitCode = await run.ExecuteAsync(await GenerateScriptAsync(seedSlot: ""));

        exitCode.ShouldNotBe(0, run.Diagnostics());
        run.Applied.ShouldBeEmpty(run.Diagnostics());
    }

    [Fact]
    public async Task Script_OldSlotLookupErrors_ExitsNonZero_AfterSwitch()
    {
        if (OperatingSystem.IsWindows()) return;

        using var run = new StubRun(serviceExists: true, activeSlot: "blue", existingDeployments: "web-blue", deploymentError: true);

        var exitCode = await run.ExecuteAsync(await GenerateScriptAsync(seedSlot: ""));

        exitCode.ShouldNotBe(0, run.Diagnostics());
        run.Steps.ShouldBe(new[] { "applied Service blue", "applied Deployment green", "rollout status deployment/web-green", "applied Service green" }, ignoreOrder: false, customMessage: run.Diagnostics());
    }

    [Fact]
    public async Task Script_NoServiceConfigured_DeploysNewSlot_LeavesOldSlotAndServicesAlone()
    {
        if (OperatingSystem.IsWindows()) return;

        using var run = new StubRun(serviceExists: true, activeSlot: "green", existingDeployments: "web-green");

        var exitCode = await run.ExecuteAsync(await GenerateScriptAsync(seedSlot: "", withService: false));

        exitCode.ShouldBe(0, run.Diagnostics());
        run.Calls.ShouldNotContain(c => c.StartsWith("get service", StringComparison.Ordinal));
        run.Steps.ShouldBe(new[] { "applied Deployment blue", "rollout status deployment/web-blue" }, ignoreOrder: false, customMessage: run.Diagnostics());
    }

    private static async Task<string> GenerateScriptAsync(string seedSlot, bool withService = true)
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers", Name = "web" };

        Add(action, "Squid.Action.KubernetesContainers.DeploymentName", "web");
        Add(action, "Squid.Action.KubernetesContainers.Namespace", "bg-ns");
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen");
        Add(action, "Squid.Action.KubernetesContainers.BlueGreenActiveSlot", seedSlot);
        Add(action, "Squid.Action.KubernetesContainers.Containers", """[{"Name":"app","Image":"busybox:latest"}]""");

        if (withService)
        {
            Add(action, "Squid.Action.KubernetesContainers.ServiceName", "web-svc");
            Add(action, "Squid.Action.KubernetesContainers.ServicePorts", """[{"name":"http","port":"80","protocol":"TCP"}]""");
        }

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

        public StubRun(bool serviceExists, string activeSlot, string existingDeployments, int rolloutExit = 0, bool serviceError = false, bool deploymentError = false)
        {
            Directory.CreateDirectory(_dir);

            var kubectl = Path.Combine(_dir, "kubectl");
            File.WriteAllText(kubectl, StubKubectl.Replace("\r\n", "\n"));

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(kubectl, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            _env = new Dictionary<string, string>
            {
                ["PATH"] = $"{_dir}:{Environment.GetEnvironmentVariable("PATH")}",
                ["STUB_LOG"] = Path.Combine(_dir, "calls.log"),
                ["STUB_APPLIED"] = Path.Combine(_dir, "applied.yaml"),
                ["STUB_SERVICE_EXISTS"] = serviceExists ? "true" : "false",
                ["STUB_ACTIVE_SLOT"] = activeSlot,
                ["STUB_EXISTING_DEPLOYMENTS"] = existingDeployments,
                ["STUB_ROLLOUT_EXIT"] = rolloutExit.ToString(),
                ["STUB_SERVICE_ERROR"] = serviceError ? "true" : "false",
                ["STUB_DEPLOYMENT_ERROR"] = deploymentError ? "true" : "false"
            };
        }

        public List<string> Calls => File.Exists(_env["STUB_LOG"]) ? File.ReadAllLines(_env["STUB_LOG"]).ToList() : new List<string>();

        /// <summary>The mutating steps only — applies, rollout waits and scale-downs — in execution order, without namespace / flag noise.</summary>
        public List<string> Steps => Calls.Where(IsStep).Select(TrimFlags).ToList();

        public string Applied => File.Exists(_env["STUB_APPLIED"]) ? File.ReadAllText(_env["STUB_APPLIED"]) : string.Empty;

        public async Task<int> ExecuteAsync(string script)
        {
            var scriptPath = Path.Combine(_dir, "bluegreen.sh");
            await File.WriteAllTextAsync(scriptPath, script);

            var startInfo = new ProcessStartInfo("bash", scriptPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

            foreach (var (key, value) in _env)
                startInfo.Environment[key] = value;

            using var process = System.Diagnostics.Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            _stdout = await stdout;
            _stderr = await stderr;

            return process.ExitCode;
        }

        public string Diagnostics() => $"kubectl calls:\n{string.Join("\n", Calls)}\n\nstdout:\n{_stdout}\n\nstderr:\n{_stderr}";

        private static bool IsStep(string call)
            => call.StartsWith("applied ", StringComparison.Ordinal) || call.StartsWith("rollout status ", StringComparison.Ordinal) || call.StartsWith("scale deployment ", StringComparison.Ordinal);

        private static string TrimFlags(string call)
            => call.StartsWith("applied ", StringComparison.Ordinal) ? call : string.Join(' ', call.Split(' ').Take(3));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
