using System.Text;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Helpers;
using Squid.E2ETests.Infrastructure;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class KubernetesContainersDeployE2ETests
    : IClassFixture<DeploymentPipelineFixture<KubernetesContainersDeployE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<KubernetesContainersDeployE2ETests> _fixture;

    public KubernetesContainersDeployE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<KubernetesContainersDeployE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Theory]
    [InlineData("KubernetesApi", true)]
    [InlineData("KubernetesApi", false)]
    [InlineData("KubernetesAgent", true)]
    [InlineData("KubernetesAgent", false)]
    public async Task FullPipeline_DeployContainers(string communicationStyle, bool createFeedSecrets)
    {
        var testNs = $"squid-e2e-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            // 1. Seed DB with complete entity graph
            var serverTaskId = await SeedDatabaseAsync(createFeedSecrets, replicas: 2, testNs, communicationStyle);

            // 2. Execute full pipeline
            await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

            // 3. Assert DB state
            await AssertTaskSuccessAsync();

            // 4. Extract YAML from captured execution requests
            var yamlFiles = YamlExtractor.Extract(ExecutionCapture);

            yamlFiles.ShouldContainKey("deployment.yaml");
            yamlFiles.ShouldContainKey("service.yaml");
            yamlFiles.ShouldContainKey("configmap.yaml");

            if (createFeedSecrets)
                yamlFiles.ShouldContainKey("feedsecrets.yaml");
            else
                yamlFiles.Keys.ShouldNotContain("feedsecrets.yaml");

            // 5. Write YAML and kubectl apply
            var tempDir = await WriteYamlToTempDirAsync(yamlFiles);

            try
            {
                await _cluster.KubectlAsync($"apply -f \"{tempDir}\" --namespace={testNs}");

                // 6. Verify Deployment
                var replicas = await _cluster.KubectlAsync(
                    $"-n {testNs} get deployment demo-nginx -o jsonpath='{{.spec.replicas}}'");
                replicas.Trim('\'').ShouldBe("2");

                var image = await _cluster.KubectlAsync(
                    $"-n {testNs} get deployment demo-nginx -o jsonpath='{{.spec.template.spec.containers[0].image}}'");
                image.Trim('\'').ShouldContain("docker.io/library/nginx:1.0.0");

                // 7. Verify Service
                var svcType = await _cluster.KubectlAsync(
                    $"-n {testNs} get service demo-service -o jsonpath='{{.spec.type}}'");
                svcType.Trim('\'').ShouldBe("ClusterIP");

                // 8. Verify ConfigMap
                var cmData = await _cluster.KubectlAsync(
                    $"-n {testNs} get configmap demo-config -o jsonpath='{{.data.APP_ENV}}'");
                cmData.Trim('\'').ShouldBe("e2e-test");

                // 9. Verify Feed Secret (conditional)
                if (createFeedSecrets)
                {
                    var pullSecrets = await _cluster.KubectlAsync(
                        $"-n {testNs} get deployment demo-nginx -o jsonpath='{{.spec.template.spec.imagePullSecrets[0].name}}'");
                    pullSecrets.Trim('\'').ShouldBe("dockerhub-registry-secret");

                    var secretType = await _cluster.KubectlAsync(
                        $"-n {testNs} get secret dockerhub-registry-secret -o jsonpath='{{.type}}'");
                    secretType.Trim('\'').ShouldBe("kubernetes.io/dockerconfigjson");

                    var secretData = await _cluster.KubectlAsync(
                        $"-n {testNs} get secret dockerhub-registry-secret -o jsonpath='{{.data.\\.dockerconfigjson}}'");
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(secretData.Trim('\'')));
                    decoded.ShouldContain("docker.io");
                    decoded.ShouldContain("testuser");
                }
                else
                {
                    var pullSecrets = await _cluster.KubectlAsync(
                        $"-n {testNs} get deployment demo-nginx -o jsonpath='{{.spec.template.spec.imagePullSecrets}}'");
                    pullSecrets.Trim('\'').ShouldBeEmpty();
                }
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task FullPipeline_DeployContainers_VariablesResolvedFromDb(string communicationStyle)
    {
        var testNs = $"squid-e2e-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var serverTaskId = await SeedDatabaseAsync(
                createFeedSecrets: false, replicas: 3, testNs, communicationStyle);

            await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

            var yamlFiles = YamlExtractor.Extract(ExecutionCapture);

            // Verify YAML contains resolved values, not templates
            var deploymentYaml = Encoding.UTF8.GetString(yamlFiles["deployment.yaml"]);
            deploymentYaml.ShouldContain($"namespace: {testNs}");
            deploymentYaml.ShouldContain("replicas: 3");
            deploymentYaml.ShouldNotContain("#{");

            var configMapYaml = Encoding.UTF8.GetString(yamlFiles["configmap.yaml"]);
            configMapYaml.ShouldContain("e2e-test");
            configMapYaml.ShouldContain("db.example.com");
            configMapYaml.ShouldNotContain("#{");

            // Apply and verify
            var tempDir = await WriteYamlToTempDirAsync(yamlFiles);

            try
            {
                await _cluster.KubectlAsync($"apply -f \"{tempDir}\" --namespace={testNs}");

                var replicas = await _cluster.KubectlAsync(
                    $"-n {testNs} get deployment demo-nginx -o jsonpath='{{.spec.replicas}}'");
                replicas.Trim('\'').ShouldBe("3");
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task FullPipeline_DeployContainers_FullPodSpec(string communicationStyle)
    {
        var testNs = $"squid-e2e-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var serverTaskId = await SeedDatabaseAsync(
                createFeedSecrets: false, replicas: 1, testNs, communicationStyle);

            await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

            var yamlFiles = YamlExtractor.Extract(ExecutionCapture);

            var tempDir = await WriteYamlToTempDirAsync(yamlFiles);

            try
            {
                await _cluster.KubectlAsync($"apply -f \"{tempDir}\" --namespace={testNs}");

                // Verify container port
                var port = await _cluster.KubectlAsync(
                    $"-n {testNs} get deployment demo-nginx -o jsonpath='{{.spec.template.spec.containers[0].ports[0].containerPort}}'");
                port.Trim('\'').ShouldBe("80");

                // Verify resource requests
                var cpuReq = await _cluster.KubectlAsync(
                    $"-n {testNs} get deployment demo-nginx -o jsonpath='{{.spec.template.spec.containers[0].resources.requests.cpu}}'");
                cpuReq.Trim('\'').ShouldBe("100m");

                var memReq = await _cluster.KubectlAsync(
                    $"-n {testNs} get deployment demo-nginx -o jsonpath='{{.spec.template.spec.containers[0].resources.requests.memory}}'");
                memReq.Trim('\'').ShouldBe("128Mi");

                // Verify volume mount
                var volumeMountPath = await _cluster.KubectlAsync(
                    $"-n {testNs} get deployment demo-nginx -o jsonpath='{{.spec.template.spec.containers[0].volumeMounts[0].mountPath}}'");
                volumeMountPath.Trim('\'').ShouldBe("/data");

                // Verify EmptyDir volume
                var volumeName = await _cluster.KubectlAsync(
                    $"-n {testNs} get deployment demo-nginx -o jsonpath='{{.spec.template.spec.volumes[0].name}}'");
                volumeName.Trim('\'').ShouldBe("data-vol");

                // Verify envFrom configMapRef
                var envFromCm = await _cluster.KubectlAsync(
                    $"-n {testNs} get deployment demo-nginx -o jsonpath='{{.spec.template.spec.containers[0].envFrom[0].configMapRef.name}}'");
                envFromCm.Trim('\'').ShouldBe("demo-config");
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private async Task<int> SeedDatabaseAsync(
        bool createFeedSecrets, int replicas, string testNs, string communicationStyle = "KubernetesApi")
    {
        // Clear capture from previous tests
        ExecutionCapture.Clear();

        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var seeder = new K8sTestDataSeeder(repository, unitOfWork);
            await seeder.SeedAsync(createFeedSecrets, replicas, testNs, communicationStyle).ConfigureAwait(false);
            serverTaskId = seeder.ServerTaskId;
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private async Task AssertTaskSuccessAsync()
    {
        await _fixture.Run<IServerTaskDataProvider, IDeploymentCompletionDataProvider>(async (taskDataProvider, completionDataProvider) =>
        {
            var tasks = await taskDataProvider.GetAllServerTasksAsync(CancellationToken.None).ConfigureAwait(false);

            tasks.ShouldNotBeNull();
            tasks.Count.ShouldBeGreaterThanOrEqualTo(1);

            var task = tasks.First(t => t.State == TaskState.Success);
            task.ShouldNotBeNull();

            var completions = await completionDataProvider.GetDeploymentCompletionsByDeploymentIdAsync(
                task.Id, CancellationToken.None).ConfigureAwait(false);

            completions.ShouldNotBeNull();
            completions.ShouldNotBeEmpty();
        }).ConfigureAwait(false);
    }

    private static async Task<string> WriteYamlToTempDirAsync(Dictionary<string, byte[]> files)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"squid-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        foreach (var file in files)
            await File.WriteAllBytesAsync(Path.Combine(tempDir, file.Key), file.Value);

        return tempDir;
    }
}
