using System.Diagnostics;
using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Script.Files;
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

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// Real-cluster proof that a <c>Squid.KubernetesDeployContainers</c> action with
/// <c>DeploymentStyle=BlueGreen</c> actually moves traffic between slots.
///
/// <para><b>Fidelity tier: High (hybrid).</b> The full Squid pipeline runs against a real
/// Postgres DB and the real <c>KubernetesApiIntentRenderer</c> / <c>KubernetesAgentIntentRenderer</c>;
/// <see cref="CapturingExecutionStrategy"/> records the rendered script, which is then executed
/// verbatim with bash + kubectl against the Kind cluster (same pattern as
/// <see cref="HelmChartUpgradeRealClusterE2ETests"/>). Only the transport hop (Halibut / local
/// process strategy) is substituted.</para>
///
/// <para>Three releases of the same project are deployed in sequence. Each container serves its
/// release number over HTTP, so after every deploy the test asserts — from inside the cluster —
/// that the Service selector, its endpoints and the actual HTTP response all point at the new
/// slot, and that the previous slot was scaled to zero. Slot alternation (green → blue → green)
/// proves the next deploy derives the live slot and targets the other one.</para>
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
[Trait("Tier", "Execution")]
public class KubernetesBlueGreenRealClusterE2ETests : IClassFixture<DeploymentPipelineFixture<KubernetesBlueGreenRealClusterE2ETests>>
{
    private const string DeploymentName = "web";
    private const string ServiceName = "web-svc";
    private const string SlotLabel = "squid.io/deployment-slot";
    private static readonly TimeSpan TrafficTimeout = TimeSpan.FromSeconds(60);

    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<KubernetesBlueGreenRealClusterE2ETests> _fixture;

    public KubernetesBlueGreenRealClusterE2ETests(KindClusterFixture cluster, DeploymentPipelineFixture<KubernetesBlueGreenRealClusterE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task BlueGreen_SuccessiveDeploys_SwitchTrafficAndScaleDownOldSlot(string communicationStyle)
    {
        var ns = $"squid-bg-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {ns}");
            await StartTrafficProbePodAsync(ns);

            var seed = await SeedProjectAsync(communicationStyle, ns);

            await DeployAndAssertAsync(seed, ns, release: "1.0.1", expectedSlot: "green", oldSlot: null);
            await DeployAndAssertAsync(seed, ns, release: "1.0.2", expectedSlot: "blue", oldSlot: "green");
            await DeployAndAssertAsync(seed, ns, release: "1.0.3", expectedSlot: "green", oldSlot: "blue");
        }
        finally
        {
            try { await _cluster.KubectlAsync($"delete namespace {ns} --ignore-not-found --wait=false"); } catch { /* best-effort */ }
        }
    }

    private async Task DeployAndAssertAsync(SeededProject seed, string ns, string release, string expectedSlot, string? oldSlot)
    {
        var serverTaskId = await SeedReleaseAsync(seed, release);

        await ExecutePipelineAsync(serverTaskId);
        await AssertTaskSuccessAsync(serverTaskId);

        var request = _fixture.ExecutionCapture.CapturedRequests.ShouldHaveSingleItem();
        var result = await RunCapturedScriptAsync(request.ScriptBody, request.DeploymentFiles);

        result.ExitCode.ShouldBe(0, $"Rendered blue/green script failed for release {release} (style {seed.CommunicationStyle}).\nSTDOUT:\n{result.StdOut}\n\nSTDERR:\n{result.StdErr}");

        await AssertSelectorAsync(ns, expectedSlot);
        await AssertEndpointsBelongToSlotAsync(ns, expectedSlot);
        await AssertTrafficServedByReleaseAsync(ns, release, expectedSlot);

        if (oldSlot != null)
            await AssertReplicasAsync(ns, $"{DeploymentName}-{oldSlot}", expected: "0");
    }

    // ── Cluster assertions ──────────────────────────────────────────────────

    private async Task AssertSelectorAsync(string ns, string expectedSlot)
    {
        var slot = await _cluster.KubectlAsync($"-n {ns} get service {ServiceName} -o jsonpath='{{.spec.selector.squid\\.io/deployment-slot}}'");

        slot.Trim('\'').Trim().ShouldBe(expectedSlot, $"Service {ServiceName} selector label {SlotLabel} should point at '{expectedSlot}'. Diagnose: kubectl -n {ns} get svc {ServiceName} -o yaml");
    }

    private async Task AssertEndpointsBelongToSlotAsync(string ns, string expectedSlot)
    {
        var endpointIps = await PollAsync(async () => SplitIps(await _cluster.KubectlAsync($"-n {ns} get endpoints {ServiceName} -o jsonpath='{{.subsets[*].addresses[*].ip}}'")), ips => ips.Count > 0);
        var slotPodIps = SplitIps(await _cluster.KubectlAsync($"-n {ns} get pods -l {SlotLabel}={expectedSlot} -o jsonpath='{{.items[*].status.podIP}}'"));

        endpointIps.ShouldNotBeEmpty($"Service {ServiceName} has no ready endpoints within {TrafficTimeout.TotalSeconds}s. Diagnose: kubectl -n {ns} get endpoints {ServiceName}");
        endpointIps.ShouldAllBe(ip => slotPodIps.Contains(ip), $"Every endpoint of {ServiceName} must be a '{expectedSlot}' pod. endpoints=[{string.Join(",", endpointIps)}] slotPods=[{string.Join(",", slotPodIps)}]");
    }

    private async Task AssertTrafficServedByReleaseAsync(string ns, string release, string expectedSlot)
    {
        var body = await PollAsync(() => FetchThroughServiceAsync(ns), b => b == release);

        body.ShouldBe(release, $"HTTP GET http://{ServiceName} from inside the cluster should be served by release {release} (slot '{expectedSlot}') within {TrafficTimeout.TotalSeconds}s. Diagnose: kubectl -n {ns} exec probe -- wget -qO- http://{ServiceName}");
    }

    private async Task AssertReplicasAsync(string ns, string deployment, string expected)
    {
        var replicas = await _cluster.KubectlAsync($"-n {ns} get deployment {deployment} -o jsonpath='{{.spec.replicas}}'");

        replicas.Trim('\'').Trim().ShouldBe(expected, $"Old slot deployment {deployment} should be scaled to {expected} after the switch. Diagnose: kubectl -n {ns} get deploy");
    }

    private async Task<string> FetchThroughServiceAsync(string ns)
    {
        try { return (await _cluster.KubectlAsync($"-n {ns} exec probe -- wget -qO- -T 3 http://{ServiceName}")).Trim(); }
        catch (InvalidOperationException) { return string.Empty; }
    }

    private async Task StartTrafficProbePodAsync(string ns)
    {
        await _cluster.KubectlAsync($"-n {ns} run probe --image=busybox:latest --image-pull-policy=IfNotPresent --restart=Never -- sleep 3600");
        await _cluster.KubectlAsync($"-n {ns} wait --for=condition=Ready pod/probe --timeout=120s");
    }

    private static List<string> SplitIps(string raw)
        => raw.Trim('\'').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static async Task<T> PollAsync<T>(Func<Task<T>> probe, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + TrafficTimeout;
        var value = await probe();

        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            value = await probe();
        }

        return value;
    }

    // ── Pipeline + execution ────────────────────────────────────────────────

    private async Task ExecutePipelineAsync(int serverTaskId)
    {
        _fixture.ExecutionCapture.Clear();

        await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(serverTaskId, CancellationToken.None));
    }

    private async Task AssertTaskSuccessAsync(int serverTaskId)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var tasks = await provider.GetAllServerTasksAsync(CancellationToken.None).ConfigureAwait(false);
            var task = tasks.Single(t => t.Id == serverTaskId);

            task.State.ShouldBe(TaskState.Success, $"Pipeline ended in state {task.State}; check activity_log for the failed action.");
        });
    }

    private async Task<ProcessResult> RunCapturedScriptAsync(string scriptBody, IReadOnlyList<DeploymentFile> files)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"squid-bg-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            foreach (var file in files)
            {
                var target = Path.Combine(workDir, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, file.Content);
            }

            var scriptPath = Path.Combine(workDir, "run.sh");
            await File.WriteAllTextAsync(scriptPath, scriptBody);

            return await RunBashAsync(scriptPath, workDir);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private async Task<ProcessResult> RunBashAsync(string scriptPath, string workDir)
    {
        var startInfo = new ProcessStartInfo("bash", scriptPath) { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        startInfo.Environment["KUBECONFIG"] = _cluster.Kubeconfig;

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, (await stdout).Trim(), (await stderr).Trim());
    }

    private record ProcessResult(int ExitCode, string StdOut, string StdErr);

    // ── Seeding ─────────────────────────────────────────────────────────────

    private record SeededProject(string CommunicationStyle, int ProjectId, int ChannelId, int EnvironmentId);

    private async Task<SeededProject> SeedProjectAsync(string communicationStyle, string ns)
    {
        var clusterUrl = (await _cluster.KubectlAsync("config view --minify -o jsonpath='{.clusters[0].cluster.server}'")).Trim('\'').Trim();
        var token = communicationStyle == "KubernetesApi" ? await MintClusterAdminTokenAsync() : string.Empty;
        SeededProject seeded = null!;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) => seeded = await SeedProjectGraphAsync(repository, unitOfWork, communicationStyle, ns, clusterUrl, token).ConfigureAwait(false));

        return seeded;
    }

    private async Task<string> MintClusterAdminTokenAsync()
    {
        const string sa = "squid-bluegreen-e2e-admin";

        try { await _cluster.KubectlAsync($"create serviceaccount {sa} -n kube-system"); } catch { /* already exists */ }
        try { await _cluster.KubectlAsync($"create clusterrolebinding {sa}-binding --clusterrole=cluster-admin --serviceaccount=kube-system:{sa}"); } catch { /* already exists */ }

        return (await _cluster.KubectlAsync($"create token {sa} -n kube-system --duration=3600s")).Trim();
    }

    private static async Task<SeededProject> SeedProjectGraphAsync(IRepository repository, IUnitOfWork unitOfWork, string communicationStyle, string ns, string clusterUrl, string token)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var builder = new TestDataBuilder(repository, unitOfWork);

        var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
        var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
        await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

        var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
        await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

        var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Blue Green").ConfigureAwait(false);
        await builder.CreateStepPropertiesAsync(step.Id, ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

        var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Deploy web", actionType: "Squid.KubernetesDeployContainers").ConfigureAwait(false);
        await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
        await builder.CreateActionPropertiesAsync(action.Id, BuildBlueGreenActionProperties(ns)).ConfigureAwait(false);

        var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
        var environment = await builder.CreateEnvironmentAsync($"E2E BlueGreen Env {suffix}").ConfigureAwait(false);

        var accountId = communicationStyle == "KubernetesApi" ? await InsertTokenAccountAsync(repository, unitOfWork, token, suffix).ConfigureAwait(false) : 0;
        await InsertMachineAsync(repository, unitOfWork, communicationStyle, environment.Id, clusterUrl, accountId, suffix).ConfigureAwait(false);

        return new SeededProject(communicationStyle, project.Id, channel.Id, environment.Id);
    }

    private static (string, string)[] BuildBlueGreenActionProperties(string ns)
    {
        // Each container serves its release number, so the HTTP body identifies which slot answered.
        var containers = JsonSerializer.Serialize(new object[]
        {
            new Dictionary<string, object>
            {
                ["Name"] = "web",
                ["Image"] = "busybox:latest",
                ["ImagePullPolicy"] = "IfNotPresent",
                ["Command"] = new[] { "sh", "-c", "mkdir -p /www && echo '#{Squid.Release.Number}' > /www/index.html && exec httpd -f -p 8080 -h /www" },
                ["Ports"] = new[] { new { key = "http", value = "8080", option = "TCP" } }
            }
        });

        return new[]
        {
            ("Squid.Action.KubernetesContainers.DeploymentName", DeploymentName),
            ("Squid.Action.KubernetesContainers.Namespace", ns),
            ("Squid.Action.KubernetesContainers.Replicas", "1"),
            ("Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen"),
            ("Squid.Action.KubernetesContainers.Containers", containers),
            ("Squid.Action.KubernetesContainers.DeploymentLabels", "{}"),
            ("Squid.Action.KubernetesContainers.ServiceName", ServiceName),
            ("Squid.Action.KubernetesContainers.ServiceType", "ClusterIP"),
            ("Squid.Action.KubernetesContainers.ServicePorts", """[{"name":"http","port":"80","targetPort":"8080","protocol":"TCP"}]"""),
            ("Squid.Action.KubernetesContainers.ObjectStatusCheckTimeout", "120")
        };
    }

    private static async Task<int> InsertTokenAccountAsync(IRepository repository, IUnitOfWork unitOfWork, string token, string suffix)
    {
        var account = new DeploymentAccount
        {
            SpaceId = 1,
            Name = $"E2E BlueGreen Account {suffix}",
            Slug = $"e2e-bluegreen-account-{suffix}",
            AccountType = AccountType.Token,
            Credentials = DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = token })
        };

        await repository.InsertAsync(account, CancellationToken.None).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return account.Id;
    }

    private static async Task InsertMachineAsync(IRepository repository, IUnitOfWork unitOfWork, string communicationStyle, int environmentId, string clusterUrl, int accountId, string suffix)
    {
        var endpointJson = communicationStyle == "KubernetesAgent"
            ? JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", SubscriptionId = Guid.NewGuid().ToString("N"), Thumbprint = "E2E-BLUEGREEN-THUMBPRINT", Namespace = "default" })
            : JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesApi", ClusterUrl = clusterUrl, SkipTlsVerification = "True", Namespace = "default", ResourceReferences = new[] { new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = accountId } } });

        var machine = new Machine
        {
            Name = $"E2E BlueGreen {communicationStyle} {suffix}",
            IsDisabled = false,
            Roles = DeploymentTargetFinder.SerializeRoles(new[] { "k8s" }),
            EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environmentId }),
            Endpoint = endpointJson,
            SpaceId = 1,
            Slug = $"e2e-bluegreen-{communicationStyle.ToLowerInvariant()}-{suffix}"
        };

        await repository.InsertAsync(machine, CancellationToken.None).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<int> SeedReleaseAsync(SeededProject seed, string version)
    {
        var taskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) => taskId = await SeedReleaseDeploymentAsync(repository, unitOfWork, seed, version).ConfigureAwait(false));

        return taskId;
    }

    private static async Task<int> SeedReleaseDeploymentAsync(IRepository repository, IUnitOfWork unitOfWork, SeededProject seed, string version)
    {
        var builder = new TestDataBuilder(repository, unitOfWork);
        var release = await builder.CreateReleaseAsync(seed.ProjectId, seed.ChannelId, version).ConfigureAwait(false);

        var deployment = new Deployment
        {
            Name = $"E2E BlueGreen {version}", SpaceId = 1, ChannelId = seed.ChannelId, ProjectId = seed.ProjectId,
            ReleaseId = release.Id, EnvironmentId = seed.EnvironmentId, DeployedBy = 1, CreatedDate = DateTimeOffset.UtcNow, Json = string.Empty
        };
        await repository.InsertAsync(deployment, CancellationToken.None).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        var serverTask = new ServerTask
        {
            Name = $"E2E BlueGreen Task {version}", Description = "E2E blue/green", QueueTime = DateTimeOffset.UtcNow, State = TaskState.Pending,
            ServerTaskType = "Deploy", ProjectId = seed.ProjectId, EnvironmentId = seed.EnvironmentId, SpaceId = 1, LastModifiedDate = DateTimeOffset.UtcNow,
            BusinessProcessState = "Queued", StateOrder = 1, Weight = 1, BatchId = 0, JSON = string.Empty, HasWarningsOrErrors = false,
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
