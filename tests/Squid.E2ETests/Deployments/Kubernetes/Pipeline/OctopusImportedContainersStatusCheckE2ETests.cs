using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Mapping.Scripts;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.E2ETests.Helpers;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Models.Deployments.Account;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for an Octopus-imported "Deploy Kubernetes containers" step: the action properties
/// are produced by the REAL import mapper (DeploymentWait=Wait + ResourceStatusCheck=False + DeploymentTimeout,
/// enabled custom resource with Octostache expressions and its own metadata.namespace), the REAL deployment
/// pipeline expands and renders the step for both Kubernetes transports, and the rendered script — its
/// kubectl context set-up, every <c>kubectl apply</c> and every status-check command — is executed with bash
/// against a real Kind cluster (KubernetesApi targets carry the cluster's real URL and a service-account
/// token). The execution strategy is the only capture (the Pipeline-pattern seam, replacing transport
/// dispatch); nothing the renderer emitted is re-implemented by the test, so a custom resource applied into
/// the wrong namespace, or a wait against the wrong namespace or name, fails the script.
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class OctopusImportedContainersStatusCheckE2ETests : IClassFixture<DeploymentPipelineFixture<OctopusImportedContainersStatusCheckE2ETests>>
{
    private const string PlaceholderClusterUrl = "https://localhost:6443";

    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<OctopusImportedContainersStatusCheckE2ETests> _fixture;

    public OctopusImportedContainersStatusCheckE2ETests(KindClusterFixture cluster, DeploymentPipelineFixture<OctopusImportedContainersStatusCheckE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task ImportedWaitStep_WithNamespacedCustomResource_AppliesExpandedResourcesAndWaitsInEachDocumentsNamespace(string communicationStyle)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var stepNamespace = $"squid-e2e-{suffix}";
        var customResourceNamespace = $"squid-e2e-cr-{suffix}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {stepNamespace}");
            await _cluster.KubectlAsync($"create namespace {customResourceNamespace}");

            var serverTaskId = await SeedImportedStepAsync(stepNamespace, customResourceNamespace, communicationStyle);

            await _fixture.Run<IDeploymentTaskExecutor>(executor => executor.ProcessAsync(serverTaskId, CancellationToken.None));

            await AssertTaskSucceededAsync(serverTaskId);

            var request = _fixture.ExecutionCapture.CapturedRequests.Single(r => r.ScriptBody.Contains("kubectl apply", StringComparison.Ordinal));
            var waits = StatusCheckCommands(request.ScriptBody);

            waits.ShouldContain($"kubectl rollout status \"deployment/demo-nginx\" -n \"{stepNamespace}\" --timeout=120s", customMessage: request.ScriptBody);
            waits.ShouldContain($"kubectl rollout status \"deployment/cr-exporter\" -n \"{customResourceNamespace}\" --timeout=120s", customMessage: request.ScriptBody);
            waits.ShouldNotContain(w => w.Contains("cr-exporter") && w.Contains(stepNamespace), customMessage: "the custom resource document declares its own namespace; waiting in the step namespace would fail with NotFound");
            waits.ShouldNotContain(w => w.Contains("not-the-name"), customMessage: "the wait must use metadata.name, not a label called 'name'");

            var run = await RunRenderedScriptAsync(request);

            run.ExitCode.ShouldBe(0, customMessage: $"The rendered apply + status-check script failed for {communicationStyle}. Re-run it manually with KUBECONFIG pointing at the Kind cluster.\nstdout:\n{run.StdOut}\nstderr:\n{run.StdErr}");

            var data = await _cluster.KubectlAsync($"-n {customResourceNamespace} get configmap e2e-test-settings -o jsonpath='{{.data}}'");
            data.ShouldContain("\"db-host\":\"db.example.com\"");
            data.ShouldContain("\"release\":\"1.0.0\"");
            data.ShouldContain("\"env\":\"E2E-TEST\"");
            (await _cluster.KubectlAsync($"-n {customResourceNamespace} get deployment cr-exporter -o name")).Trim().ShouldBe("deployment.apps/cr-exporter");
            (await _cluster.KubectlAsync($"-n {stepNamespace} get deployment demo-nginx -o name")).Trim().ShouldBe("deployment.apps/demo-nginx");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {stepNamespace} --ignore-not-found --wait=false");
            await _cluster.KubectlAsync($"delete namespace {customResourceNamespace} --ignore-not-found --wait=false");
        }
    }

    // ── Seeding: the K8s scenario + the properties the import mapper produces for the Octopus step ──

    private async Task<int> SeedImportedStepAsync(string stepNamespace, string customResourceNamespace, string communicationStyle)
    {
        _fixture.ExecutionCapture.Clear();

        var clusterUrl = (await _cluster.KubectlAsync("config view --minify -o jsonpath='{.clusters[0].cluster.server}'")).Trim('\'').Trim();
        var clusterToken = await CreateServiceAccountTokenAsync();
        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork, IDeploymentAccountDataProvider>(async (repository, unitOfWork, accounts) =>
        {
            var seeder = new K8sTestDataSeeder(repository, unitOfWork, accounts);
            await seeder.SeedAsync(createFeedSecrets: false, replicas: 0, stepNamespace, communicationStyle, credentials: new TokenCredentials { Token = clusterToken }).ConfigureAwait(false);
            serverTaskId = seeder.ServerTaskId;

            await PointApiTargetsAtKindAsync(repository, unitOfWork, clusterUrl).ConfigureAwait(false);

            var action = await repository.Query<DeploymentAction>(a => a.Name == "Deploy demo").OrderByDescending(a => a.Id).FirstAsync().ConfigureAwait(false);
            await new TestDataBuilder(repository, unitOfWork).CreateActionPropertiesAsync(action.Id, ImportedProperties(customResourceNamespace)).ConfigureAwait(false);
        });

        return serverTaskId;
    }

    private static (string Name, string Value)[] ImportedProperties(string customResourceNamespace)
    {
        var octopusAction = new OctopusDeploymentActionDto { Id = "Actions-1", Name = "Deploy demo", ActionType = "Octopus.KubernetesDeployContainers", Properties = new() };

        octopusAction.Properties["Octopus.Action.EnabledFeatures"] = "Octopus.Features.KubernetesService,Octopus.Features.KubernetesConfigMap,Octopus.Features.KubernetesCustomResource";
        octopusAction.Properties["Octopus.Action.KubernetesContainers.DeploymentWait"] = "Wait";
        octopusAction.Properties["Octopus.Action.Kubernetes.ResourceStatusCheck"] = "False";
        octopusAction.Properties["Octopus.Action.Kubernetes.DeploymentTimeout"] = "120";
        octopusAction.Properties["Octopus.Action.KubernetesContainers.CustomResourceYaml"] = CustomResourceYaml(customResourceNamespace);

        var mapped = new OctopusKubernetesDeployContainersActionMapper().Map(octopusAction, new OctopusImportActionMappingContext(new OctopusImportIdMap(), 1)).Action;

        // The import's process mapper rewrites Octopus built-ins on every mapped property (SSOT helper).
        return mapped.Properties.Select(p => (p.PropertyName, OctopusSystemVariables.RewriteReferences(p.PropertyValue))).ToArray();
    }

    private static string CustomResourceYaml(string customResourceNamespace) => $$"""
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: #{AppEnv}-settings
          namespace: {{customResourceNamespace}}
        data:
          db-host: "#{DbHost}"
          release: "#{Octopus.Release.Number}"
        #{if AppEnv}
          env: "#{AppEnv | ToUpper}"
        #{/if}
        ---
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          labels:
            name: not-the-name
          name: cr-exporter
          namespace: {{customResourceNamespace}}
        spec:
          replicas: 0
          selector:
            matchLabels:
              app: cr-exporter
          template:
            metadata:
              labels:
                app: cr-exporter
            spec:
              containers:
                - name: exporter
                  image: busybox:latest
                  imagePullPolicy: IfNotPresent
        """;

    // ── Assertions / cluster helpers ─────────────────────────────────────────────────────────────

    private static List<string> StatusCheckCommands(string script)
        => script.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("kubectl rollout status ", StringComparison.Ordinal) || l.StartsWith("kubectl wait ", StringComparison.Ordinal)).ToList();

    private async Task AssertTaskSucceededAsync(int serverTaskId)
    {
        await _fixture.Run<IServerTaskDataProvider>(async tasks =>
        {
            var task = await tasks.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);

            task.State.ShouldBe(TaskState.Success);
        });
    }

    // K8sTestDataSeeder points KubernetesApi targets at a placeholder URL; the rendered kubectl context
    // must reach the real Kind API server for the script to run.
    private static async Task PointApiTargetsAtKindAsync(IRepository repository, IUnitOfWork unitOfWork, string clusterUrl)
    {
        var machines = await repository.Query<Machine>().ToListAsync().ConfigureAwait(false);

        foreach (var machine in machines.Where(m => m.Endpoint.Contains(PlaceholderClusterUrl, StringComparison.Ordinal)))
        {
            machine.Endpoint = machine.Endpoint.Replace(PlaceholderClusterUrl, clusterUrl, StringComparison.Ordinal);
            await repository.UpdateAsync(machine).ConfigureAwait(false);
        }

        await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<string> CreateServiceAccountTokenAsync()
    {
        const string serviceAccount = "squid-import-e2e-admin";

        try { await _cluster.KubectlAsync($"create serviceaccount {serviceAccount} -n kube-system"); } catch { /* already exists */ }
        try { await _cluster.KubectlAsync($"create clusterrolebinding {serviceAccount}-binding --clusterrole=cluster-admin --serviceaccount=kube-system:{serviceAccount}"); } catch { /* already exists */ }

        return (await _cluster.KubectlAsync($"create token {serviceAccount} -n kube-system --duration=3600s")).Trim();
    }

    // Stages the rendered files and runs the rendered script exactly as the agent would. The Agent preamble
    // switches the kubeconfig's current namespace, so it runs on a private copy of the Kind kubeconfig.
    private async Task<ProcessResult> RunRenderedScriptAsync(ScriptExecutionRequest request)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"squid-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            foreach (var file in request.DeploymentFiles)
                await StageFileAsync(directory, file.RelativePath, file.Content);

            var kubeconfig = Path.Combine(directory, ".kubeconfig");
            File.Copy(_cluster.Kubeconfig, kubeconfig);
            await File.WriteAllTextAsync(Path.Combine(directory, "run.sh"), request.ScriptBody);

            return await RunBashAsync(directory, kubeconfig);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static async Task StageFileAsync(string directory, string relativePath, byte[] content)
    {
        var path = Path.Combine(directory, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
    }

    private static async Task<ProcessResult> RunBashAsync(string directory, string kubeconfig)
    {
        var startInfo = new ProcessStartInfo { FileName = "bash", Arguments = "run.sh", WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        startInfo.Environment["KUBECONFIG"] = kubeconfig;

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Rendered script did not finish within 5 minutes in {directory}; check `kubectl get pods -A` on the Kind cluster.");
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
