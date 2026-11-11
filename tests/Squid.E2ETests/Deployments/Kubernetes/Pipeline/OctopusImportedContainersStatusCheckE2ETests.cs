using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Mapping.Scripts;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.E2ETests.Helpers;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

/// <summary>
/// High-fidelity tier for an Octopus-imported "Deploy Kubernetes containers" step: the action properties
/// are produced by the REAL import mapper (DeploymentWait=Wait + ResourceStatusCheck=False + DeploymentTimeout,
/// enabled custom resource with Octostache expressions and its own metadata.namespace), the REAL deployment
/// pipeline expands and renders the step for both Kubernetes transports, the rendered YAML is applied to a
/// real Kind cluster, and every status-check command the renderer emitted is executed against that cluster.
/// Only the execution strategy is a capture (the Pipeline-pattern seam); the commands it captured are then run
/// for real, so a wrong namespace or name fails with NotFound.
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class OctopusImportedContainersStatusCheckE2ETests : IClassFixture<DeploymentPipelineFixture<OctopusImportedContainersStatusCheckE2ETests>>
{
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

            var script = _fixture.ExecutionCapture.CapturedRequests.Single(r => r.ScriptBody.Contains("kubectl apply", StringComparison.Ordinal)).ScriptBody;
            var waits = StatusCheckCommands(script);

            waits.ShouldContain($"kubectl rollout status \"deployment/demo-nginx\" -n \"{stepNamespace}\" --timeout=120s", customMessage: script);
            waits.ShouldContain($"kubectl rollout status \"deployment/cr-exporter\" -n \"{customResourceNamespace}\" --timeout=120s", customMessage: script);
            waits.ShouldNotContain(w => w.Contains("cr-exporter") && w.Contains(stepNamespace), customMessage: "the custom resource document declares its own namespace; waiting in the step namespace would fail with NotFound");
            waits.ShouldNotContain(w => w.Contains("not-the-name"), customMessage: "the wait must use metadata.name, not a label called 'name'");

            await ApplyAsync(YamlExtractor.Extract(_fixture.ExecutionCapture), stepNamespace);

            var data = await _cluster.KubectlAsync($"-n {customResourceNamespace} get configmap e2e-test-settings -o jsonpath='{{.data}}'");
            data.ShouldContain("\"db-host\":\"db.example.com\"");
            data.ShouldContain("\"release\":\"1.0.0\"");
            data.ShouldContain("\"env\":\"E2E-TEST\"");
            (await _cluster.KubectlAsync($"-n {customResourceNamespace} get deployment cr-exporter -o name")).Trim().ShouldBe("deployment.apps/cr-exporter");

            foreach (var wait in waits)
                await _cluster.KubectlAsync(wait["kubectl ".Length..]);
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

        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork, IDeploymentAccountDataProvider>(async (repository, unitOfWork, accounts) =>
        {
            var seeder = new K8sTestDataSeeder(repository, unitOfWork, accounts);
            await seeder.SeedAsync(createFeedSecrets: false, replicas: 0, stepNamespace, communicationStyle).ConfigureAwait(false);
            serverTaskId = seeder.ServerTaskId;

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

    // Workload files carry the step namespace; the custom resource carries its own, so it is applied
    // without a --namespace override (kubectl rejects a flag that disagrees with metadata.namespace).
    private async Task ApplyAsync(Dictionary<string, byte[]> yamlFiles, string stepNamespace)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"squid-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            foreach (var (name, content) in yamlFiles)
                await File.WriteAllBytesAsync(Path.Combine(directory, name), content);

            foreach (var name in yamlFiles.Keys.Where(n => n != "customresource.yaml").Order(StringComparer.Ordinal))
                await _cluster.KubectlAsync($"apply -f \"{Path.Combine(directory, name)}\" --namespace={stepNamespace}");

            await _cluster.KubectlAsync($"apply -f \"{Path.Combine(directory, "customresource.yaml")}\"");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
