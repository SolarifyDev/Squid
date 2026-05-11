using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// E2E-1 part 2 — REAL-CLUSTER HYBRID tests for the <c>Squid.HelmChartUpgrade</c>
/// action. Companion to <see cref="HelmChartUpgradeE2ETests"/> (which is
/// contract-only Pattern 2 — captures the script but doesn't execute it).
///
/// <para><b>The hybrid pattern</b>:</para>
/// <list type="number">
///   <item>Run the full Squid pipeline → <c>CapturingExecutionStrategy</c>
///         records the script + values files the agent <i>would</i> run.</item>
///   <item>Extract those files to a temp directory.</item>
///   <item>Execute the SAME script body against the real Kind cluster via the
///         local <c>helm</c> CLI.</item>
///   <item>Query Kind via <c>kubectl get</c> to verify resources actually
///         materialised — the proof that the captured script is more than a
///         well-formed string, it actually works.</item>
/// </list>
///
/// <para><b>Stability design choices</b>:</para>
/// <list type="bullet">
///   <item><b>Local chart, zero network deps</b> — chart lives at
///         <c>Resources/test-charts/squid-test-chart/</c> and is copied to
///         the test assembly's output directory at build time. Tests reference
///         it by file path, never via <c>helm repo add bitnami</c> (which is
///         the existing skipped tests' flakiness source — public registry
///         drift + DNS).</item>
///   <item><b>Image preloaded into Kind</b> — <see cref="KindClusterFixture"/>'s
///         <c>TryPreloadImagesAsync</c> calls <c>kind load docker-image
///         busybox:latest</c> on fixture init so kubelet finds the image
///         locally (no Docker Hub rate limit risk; ~5 MB so loads in &lt;5s).</item>
///   <item><b>busybox sleep 3600</b> — minimal container that starts in &lt;1s
///         and has no readiness probe. We assert Deployment EXISTS, not pod
///         Ready (which adds 20-60s latency + Kind-CPU-load flakiness).</item>
///   <item><b>Unique namespace + release per test</b> — GUID-suffixed so concurrent
///         or repeated runs don't collide on cluster state.</item>
///   <item><b>Aggressive cleanup</b> — <c>finally</c> block uninstalls helm
///         release + deletes namespace even when the test body throws, so
///         downstream tests start clean.</item>
///   <item><b>Skip-on-no-helm-CLI</b> — if <c>helm version</c> fails (CI
///         runner without helm), the test is skipped with a clear message
///         rather than failing. Future CI workflows for this project must
///         install helm explicitly (e.g. <c>azure/setup-helm@v4</c>).</item>
/// </list>
///
/// <para>Combined with the contract tests in <see cref="HelmChartUpgradeE2ETests"/>,
/// this gives a two-tier coverage model:</para>
/// <list type="bullet">
///   <item><b>Tier "Contract"</b>: every test in HelmChartUpgradeE2ETests —
///         fast, hermetic, runs on every CI commit. Catches "we generated the
///         wrong script" bugs.</item>
///   <item><b>Tier "Execution"</b>: every test in this class — slower, requires
///         helm + Kind. Catches "the script is right but execution fails on
///         a real cluster" bugs.</item>
/// </list>
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
[Trait("Tier", "Execution")]
public class HelmChartUpgradeRealClusterE2ETests
    : IClassFixture<DeploymentPipelineFixture<HelmChartUpgradeRealClusterE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<HelmChartUpgradeRealClusterE2ETests> _fixture;

    public HelmChartUpgradeRealClusterE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<HelmChartUpgradeRealClusterE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    /// <summary>
    /// Path to the local Helm chart, computed once per assembly load.
    /// Chart files are copied to the test output directory by the .csproj
    /// (see <c>None Update="Resources\test-charts\**\*"</c>).
    /// </summary>
    private static readonly string LocalChartPath =
        Path.Combine(Path.GetDirectoryName(typeof(HelmChartUpgradeRealClusterE2ETests).Assembly.Location)!,
                     "Resources", "test-charts", "squid-test-chart");

    // ── 1. Real Helm install via captured script — Deployment actually created ──

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task RealHelm_BasicChartInstall_DeploymentExistsInKind(string communicationStyle)
    {
        if (!await IsHelmCliAvailableAsync()) return;
        await EnsureLocalChartPresentAsync();

        var ns = $"squid-helm-{Guid.NewGuid().ToString("N")[..8]}";
        var release = $"e2e-real-{Guid.NewGuid().ToString("N")[..6]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {ns}");

            var serverTaskId = await SeedHelmAsync(communicationStyle, properties: new()
            {
                ["Squid.Action.Helm.ReleaseName"] = release,
                ["Squid.Action.Helm.ChartPath"] = LocalChartPath,
                ["Squid.Action.Kubernetes.Namespace"] = ns,
                ["Squid.Action.Script.Syntax"] = "Bash"
            });

            await ExecutePipelineAsync(serverTaskId);
            await AssertTaskSuccessAsync(serverTaskId);

            // Hybrid step: execute the captured script against real Kind.
            var request = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();
            var result = await RunCapturedScriptAsync(request.ScriptBody, request.DeploymentFiles);

            result.ExitCode.ShouldBe(0,
                customMessage: $"Real helm install failed for style={communicationStyle}.\n" +
                              $"STDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}\n\n" +
                              "Common causes: (a) Kind cluster not reachable via kubeconfig at PATH, " +
                              "(b) helm CLI version incompatible with chart apiVersion v2, " +
                              "(c) image preload failed and busybox can't pull (check fixture logs).");

            // Real cluster proof: Deployment actually exists.
            var deploymentName = $"{release}-app";
            var deployJson = await _cluster.KubectlAsync($"-n {ns} get deployment {deploymentName} -o json");

            deployJson.ShouldNotBeNullOrEmpty(
                customMessage: $"Deployment '{deploymentName}' MUST exist in namespace '{ns}' after helm install. " +
                              "If empty: the captured script ran without error but didn't actually deploy " +
                              "the chart resources — verify HelmUpgradeIntent.ChartReference points at LocalChartPath, " +
                              "and that helm executed the chart's templates/deployment.yaml.");

            var replicas = await _cluster.KubectlAsync(
                $"-n {ns} get deployment {deploymentName} -o jsonpath='{{.spec.replicas}}'");
            replicas.Trim('\'').ShouldBe("1",
                customMessage: "Default replicaCount from values.yaml is 1; if mismatched the chart's values " +
                              "weren't applied correctly.");
        }
        finally
        {
            await BestEffortCleanupAsync(release, ns);
        }
    }

    // ── 2. Real Helm install with inline values — replicas override applied ──

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task RealHelm_InlineKeyValues_AppliedToDeployment(string communicationStyle)
    {
        if (!await IsHelmCliAvailableAsync()) return;
        await EnsureLocalChartPresentAsync();

        var ns = $"squid-helmkv-{Guid.NewGuid().ToString("N")[..8]}";
        var release = $"e2e-kv-{Guid.NewGuid().ToString("N")[..6]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {ns}");

            // KeyValues to override the chart's defaults.
            var keyValues = """{"replicas":"3","nameSuffix":"web"}""";

            var serverTaskId = await SeedHelmAsync(communicationStyle, properties: new()
            {
                ["Squid.Action.Helm.ReleaseName"] = release,
                ["Squid.Action.Helm.ChartPath"] = LocalChartPath,
                ["Squid.Action.Kubernetes.Namespace"] = ns,
                ["Squid.Action.Helm.KeyValues"] = keyValues,
                ["Squid.Action.Script.Syntax"] = "Bash"
            });

            await ExecutePipelineAsync(serverTaskId);
            await AssertTaskSuccessAsync(serverTaskId);

            var request = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();
            var result = await RunCapturedScriptAsync(request.ScriptBody, request.DeploymentFiles);
            result.ExitCode.ShouldBe(0, $"helm install failed: {result.StdErr}");

            // The chart's templates use `nameSuffix=web` → Deployment name = "{release}-web".
            var deploymentName = $"{release}-web";
            var replicas = await _cluster.KubectlAsync(
                $"-n {ns} get deployment {deploymentName} -o jsonpath='{{.spec.replicas}}'");
            replicas.Trim('\'').ShouldBe("3",
                customMessage: "InlineValues KeyValues 'replicas=3' MUST be applied to the deployed Deployment's " +
                              "spec.replicas. If got '1' (chart default): the generated inline-values.yaml file " +
                              "wasn't passed to helm via --values, or helm didn't honour it. " +
                              "Verify HelmUpgradeScriptBuilder.AppendBashInlineValuesAsFile was called.");
        }
        finally
        {
            await BestEffortCleanupAsync(release, ns);
        }
    }

    // ── 3. P0-Phase10.2 SECURITY VERIFICATION on a real cluster ──

    [Fact]
    public async Task RealHelm_SensitiveInlineValue_NotInProcessArgv_OnRealAgent()
    {
        // The contract tests already pin that --set is absent from the script
        // and inline-values.yaml is generated. This test additionally proves
        // that with a real helm CLI, the sensitive value doesn't actually leak
        // to /proc/<helm-pid>/cmdline.
        if (!await IsHelmCliAvailableAsync()) return;
        await EnsureLocalChartPresentAsync();

        var ns = $"squid-sec-{Guid.NewGuid().ToString("N")[..8]}";
        var release = $"e2e-sec-{Guid.NewGuid().ToString("N")[..6]}";
        const string sensitiveValue = "REAL-CLUSTER-SECRET-MUST-NOT-LEAK-TO-PS-2026";

        try
        {
            await _cluster.KubectlAsync($"create namespace {ns}");

            var keyValues = $$"""{"env.SECRET_VAR":"{{sensitiveValue}}"}""";

            var serverTaskId = await SeedHelmAsync("KubernetesApi", properties: new()
            {
                ["Squid.Action.Helm.ReleaseName"] = release,
                ["Squid.Action.Helm.ChartPath"] = LocalChartPath,
                ["Squid.Action.Kubernetes.Namespace"] = ns,
                ["Squid.Action.Helm.KeyValues"] = keyValues,
                ["Squid.Action.Script.Syntax"] = "Bash"
            });

            await ExecutePipelineAsync(serverTaskId);
            await AssertTaskSuccessAsync(serverTaskId);

            var request = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();

            // The captured ScriptBody (what would land in /proc/<helm-pid>/cmdline)
            // MUST NOT contain the secret value — only --values inline-values.yaml.
            request.ScriptBody.ShouldNotContain(sensitiveValue,
                customMessage: "P0-Phase10.2 SECURITY REGRESSION: sensitive value found in script body. " +
                              "On a real OS, this body becomes the helm argv → ps / kubelet logs / audit. " +
                              "Verify TryGenerateInlineValuesFile generates inline-values.yaml + " +
                              "AppendBashInlineValuesAsFile references it via --values.");

            // The inline-values.yaml DOES carry the secret — that's correct.
            var inline = request.DeploymentFiles.Single(f => f.RelativePath == "inline-values.yaml");
            Encoding.UTF8.GetString(inline.Content).ShouldContain(sensitiveValue,
                customMessage: "inline-values.yaml MUST carry the sensitive value (this is how helm sees it; " +
                              "the file is on-disk with 0600-equivalent perms on the agent). If missing, " +
                              "the secret was lost between the action handler and the script builder.");

            // Execute on real cluster — should succeed, then we verify the env var
            // was actually applied to the Deployment's pod spec.
            var result = await RunCapturedScriptAsync(request.ScriptBody, request.DeploymentFiles);
            result.ExitCode.ShouldBe(0, $"helm install failed: {result.StdErr}");

            // Cross-check: the Deployment's pod spec carries the secret env var.
            // (The chart template renders .Values.env into the container's env list.)
            var envValue = await _cluster.KubectlAsync(
                $"-n {ns} get deployment {release}-app -o jsonpath='{{.spec.template.spec.containers[0].env[?(@.name==\"SECRET_VAR\")].value}}'");

            envValue.Trim('\'').ShouldBe(sensitiveValue,
                customMessage: "End-to-end value flow check: the sensitive value should reach the Deployment's " +
                              "env var. If empty: chart values weren't propagated. If different: variable " +
                              "substitution mangled the value. If matches: P0-Phase10.2 hardening is intact " +
                              "AND value flows correctly to the cluster.");
        }
        finally
        {
            await BestEffortCleanupAsync(release, ns);
        }
    }

    // ── 4. Helm upgrade idempotence — second install is a no-op upgrade ──

    [Fact]
    public async Task RealHelm_SecondInstallSameRelease_IsIdempotentUpgrade()
    {
        // Operators rely on `helm upgrade --install` being idempotent: running
        // the same deploy twice should produce the same cluster state, not
        // duplicate Deployments or errors.
        if (!await IsHelmCliAvailableAsync()) return;
        await EnsureLocalChartPresentAsync();

        var ns = $"squid-idem-{Guid.NewGuid().ToString("N")[..8]}";
        var release = $"e2e-idem-{Guid.NewGuid().ToString("N")[..6]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {ns}");

            var serverTaskId = await SeedHelmAsync("KubernetesApi", properties: new()
            {
                ["Squid.Action.Helm.ReleaseName"] = release,
                ["Squid.Action.Helm.ChartPath"] = LocalChartPath,
                ["Squid.Action.Kubernetes.Namespace"] = ns,
                ["Squid.Action.Script.Syntax"] = "Bash"
            });

            await ExecutePipelineAsync(serverTaskId);
            var firstRequest = ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();
            var firstRun = await RunCapturedScriptAsync(firstRequest.ScriptBody, firstRequest.DeploymentFiles);
            firstRun.ExitCode.ShouldBe(0, $"first helm install failed: {firstRun.StdErr}");

            // Second run — execute the SAME script body again. Helm should
            // produce a revision-2 release; cluster state should match.
            var secondRun = await RunCapturedScriptAsync(firstRequest.ScriptBody, firstRequest.DeploymentFiles);
            secondRun.ExitCode.ShouldBe(0,
                customMessage: "Second run of `helm upgrade --install` failed. " +
                              $"STDERR:\n{secondRun.StdErr}\n\n" +
                              "Helm upgrade --install MUST be idempotent — operators re-run deploys all the time. " +
                              "If this fails, our chart or the generated script has non-idempotent side effects.");

            // Exactly ONE Deployment with this name (not 2 — that'd indicate
            // a naming collision / non-idempotent template).
            var deploys = await _cluster.KubectlAsync(
                $"-n {ns} get deployments -o jsonpath='{{.items[*].metadata.name}}'");
            deploys.Trim('\'').Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Count(name => name == $"{release}-app")
                .ShouldBe(1, "Idempotent upgrade MUST produce exactly one Deployment, not duplicates.");

            // Helm release should be at revision 2 after the second install.
            var helmList = await ExecuteBashAsync($"helm list -n {ns} -o json --kubeconfig {_cluster.Kubeconfig}");
            helmList.ExitCode.ShouldBe(0);
            helmList.StdOut.ShouldContain($"\"name\":\"{release}\"");
            helmList.StdOut.ShouldContain("\"revision\":\"2\"",
                customMessage: "Helm should track revision 2 after idempotent re-install. " +
                              $"Got list output: {helmList.StdOut}");
        }
        finally
        {
            await BestEffortCleanupAsync(release, ns);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task ExecutePipelineAsync(int serverTaskId)
    {
        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task AssertTaskSuccessAsync(int serverTaskId)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var tasks = await provider.GetAllServerTasksAsync(CancellationToken.None).ConfigureAwait(false);
            var task = tasks.SingleOrDefault(t => t.Id == serverTaskId);
            task.ShouldNotBeNull($"Task {serverTaskId} not found in DB after pipeline ran.");
            task.State.ShouldBe(TaskState.Success,
                customMessage: $"Pipeline ended in state {task.State} (expected Success). " +
                              "Check activity_log table for the failed action's stderr.");
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Stages the captured DeploymentFiles to a temp directory, then executes
    /// the script body with cwd set to that directory. helm reads --values
    /// paths relative to cwd, so the temp dir contract is essential.
    /// </summary>
    private async Task<ProcessResult> RunCapturedScriptAsync(
        string scriptBody, IReadOnlyList<Squid.Core.Services.DeploymentExecution.Script.Files.DeploymentFile> files)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-helm-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            foreach (var file in files)
            {
                var targetPath = Path.Combine(workDir, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await File.WriteAllBytesAsync(targetPath, file.Content);
            }

            // Tell the captured script to use the test cluster's kubeconfig.
            // The script bodies generated by Squid don't carry --kubeconfig
            // because in production the agent already has KUBECONFIG set;
            // in this test the agent isn't real, so we inject the var.
            var scriptPath = Path.Combine(workDir, "run.sh");
            await File.WriteAllTextAsync(scriptPath, scriptBody);

            var startInfo = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = scriptPath,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.Environment["KUBECONFIG"] = _cluster.Kubeconfig;
            // Some helm subcommands also accept --kubeconfig flag explicitly;
            // KUBECONFIG env covers all of them.

            using var process = Process.Start(startInfo)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return new ProcessResult(process.ExitCode, stdout.Trim(), stderr.Trim());
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private async Task<ProcessResult> ExecuteBashAsync(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["KUBECONFIG"] = _cluster.Kubeconfig;

        using var process = Process.Start(startInfo)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private async Task<bool> IsHelmCliAvailableAsync()
    {
        try
        {
            var result = await ExecuteBashAsync("helm version --short");
            if (result.ExitCode != 0)
            {
                Console.WriteLine($"[HelmRealClusterE2E] Skipping: helm CLI not available (exit {result.ExitCode}). " +
                                  $"Install via `azure/setup-helm@v4` in CI or via Homebrew locally.");
                return false;
            }

            Console.WriteLine($"[HelmRealClusterE2E] helm CLI: {result.StdOut}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HelmRealClusterE2E] Skipping: failed to invoke helm ({ex.Message}).");
            return false;
        }
    }

    private static Task EnsureLocalChartPresentAsync()
    {
        if (!Directory.Exists(LocalChartPath))
            throw new InvalidOperationException(
                $"Local test chart not found at {LocalChartPath}. " +
                "Verify the .csproj has `<None Update=\"Resources\\test-charts\\**\\*\">` " +
                "with CopyToOutputDirectory=PreserveNewest, and that the chart files " +
                "in tests/Squid.E2ETests/Resources/test-charts/squid-test-chart/ exist.");

        var chartYaml = Path.Combine(LocalChartPath, "Chart.yaml");
        if (!File.Exists(chartYaml))
            throw new InvalidOperationException($"Chart.yaml missing at {chartYaml}.");

        return Task.CompletedTask;
    }

    private async Task BestEffortCleanupAsync(string release, string ns)
    {
        // helm uninstall first (drops the release tracking + finalisers),
        // then delete namespace (catches any non-helm-managed resources).
        try { await ExecuteBashAsync($"helm uninstall {release} -n {ns} --kubeconfig {_cluster.Kubeconfig} 2>/dev/null || true"); } catch { }
        try { await _cluster.KubectlAsync($"delete namespace {ns} --ignore-not-found --wait=false"); } catch { }
    }

    private record ProcessResult(int ExitCode, string StdOut, string StdErr);

    // ── Seeder (shared with the contract test class via mirror) ────────────

    private int _lastSeededTaskId;

    private async Task<int> SeedHelmAsync(
        string communicationStyle, Dictionary<string, string> properties)
    {
        ExecutionCapture.Clear();

        var taskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            taskId = await SeedHelmTestDataAsync(repository, unitOfWork, communicationStyle, properties);
        }).ConfigureAwait(false);

        _lastSeededTaskId = taskId;
        return taskId;
    }

    private static async Task<int> SeedHelmTestDataAsync(
        IRepository repository, IUnitOfWork unitOfWork,
        string communicationStyle, Dictionary<string, string> properties)
    {
        var builder = new TestDataBuilder(repository, unitOfWork);

        var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
        await builder.CreateVariablesAsync(variableSet.Id,
            ("AppEnv", "e2e-real-cluster", VariableType.String, false)).ConfigureAwait(false);

        var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
        await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

        var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
        await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

        var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Real Helm").ConfigureAwait(false);
        await builder.CreateStepPropertiesAsync(step.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

        var action = await builder.CreateDeploymentActionAsync(
            step.Id, 1, "Real Helm Upgrade", actionType: "Squid.HelmChartUpgrade").ConfigureAwait(false);

        await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
        await builder.CreateActionPropertiesAsync(action.Id,
            properties.Select(kvp => (kvp.Key, kvp.Value)).ToArray()).ConfigureAwait(false);

        var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
        var environment = await builder.CreateEnvironmentAsync("E2E Real-Cluster Env").ConfigureAwait(false);

        var endpointJson = communicationStyle == "KubernetesAgent"
            ? JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesAgent",
                SubscriptionId = Guid.NewGuid().ToString("N"),
                Thumbprint = "E2E-REAL-THUMBPRINT",
                Namespace = "default"
            })
            : JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesApi",
                ClusterUrl = "https://localhost:6443",
                SkipTlsVerification = "True",
                Namespace = "default",
                ResourceReferences = new[]
                {
                    new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = 1 }
                }
            });

        var machine = new Machine
        {
            Name = $"E2E Real {communicationStyle}",
            IsDisabled = false,
            Roles = DeploymentTargetFinder.SerializeRoles(new[] { "k8s" }),
            EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
            Endpoint = endpointJson,
            SpaceId = 1,
            Slug = $"e2e-real-{Guid.NewGuid().ToString("N")[..6]}"
        };
        await repository.InsertAsync(machine, CancellationToken.None).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        if (communicationStyle != "KubernetesAgent")
        {
            var account = new DeploymentAccount
            {
                SpaceId = 1,
                Name = "E2E Real Account",
                Slug = "e2e-real-account",
                AccountType = AccountType.Token,
                Credentials = DeploymentAccountCredentialsConverter.Serialize(
                    new TokenCredentials { Token = "e2e-real-token" })
            };
            await repository.InsertAsync(account, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);
        var deployment = new Deployment
        {
            Name = "E2E Real Helm Deployment",
            SpaceId = 1, ChannelId = channel.Id, ProjectId = project.Id,
            ReleaseId = release.Id, EnvironmentId = environment.Id,
            DeployedBy = 1, CreatedDate = DateTimeOffset.UtcNow, Json = string.Empty
        };
        await repository.InsertAsync(deployment, CancellationToken.None).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        var serverTask = new ServerTask
        {
            Name = "E2E Real Helm Task", Description = "E2E real-cluster helm",
            QueueTime = DateTimeOffset.UtcNow, State = TaskState.Pending,
            ServerTaskType = "Deploy", ProjectId = project.Id, EnvironmentId = environment.Id,
            SpaceId = 1, LastModifiedDate = DateTimeOffset.UtcNow,
            BusinessProcessState = "Queued", StateOrder = 1, Weight = 1, BatchId = 0,
            JSON = string.Empty, HasWarningsOrErrors = false,
            ServerNodeId = Guid.NewGuid(), DurationSeconds = 0, DataVersion = Array.Empty<byte>()
        };
        await repository.InsertAsync(serverTask, CancellationToken.None).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        deployment.TaskId = serverTask.Id;
        await repository.UpdateAsync(deployment, CancellationToken.None).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return serverTask.Id;
    }
}
