using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Helpers;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class KubernetesIngressDeployE2ETests
    : IClassFixture<DeploymentPipelineFixture<KubernetesIngressDeployE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<KubernetesIngressDeployE2ETests> _fixture;

    public KubernetesIngressDeployE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<KubernetesIngressDeployE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task FullPipeline_DeployIngress_GeneratesIngressYaml(string communicationStyle)
    {
        var testNs = $"squid-ing-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var serverTaskId = await SeedIngressAsync(testNs, communicationStyle);

            await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await AssertTaskSuccessAsync();

            ExecutionCapture.CapturedRequests.ShouldNotBeEmpty("Pipeline should have executed at least one script");

            var capturedRequest = ExecutionCapture.CapturedRequests[0];
            capturedRequest.ScriptBody.ShouldContain("kubectl apply");

            var yamlFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in capturedRequest.Files)
            {
                if (file.Key.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                    yamlFiles[file.Key] = file.Value;
            }

            yamlFiles.ShouldContainKey("ingress.yaml");

            var ingressYaml = Encoding.UTF8.GetString(yamlFiles["ingress.yaml"]);
            ingressYaml.ShouldContain("apiVersion: networking.k8s.io/v1");
            ingressYaml.ShouldContain("kind: Ingress");
            ingressYaml.ShouldContain("name: web-ingress");
            ingressYaml.ShouldContain($"namespace: {testNs}");
            ingressYaml.ShouldContain("app.example.com");
            ingressYaml.ShouldContain("ingressClassName: nginx");

            // Apply to Kind cluster
            var tempDir = await WriteYamlToTempDirAsync(yamlFiles);

            try
            {
                await _cluster.KubectlAsync($"apply -f \"{tempDir}\" --namespace={testNs}");

                var ingressName = await _cluster.KubectlAsync(
                    $"-n {testNs} get ingress web-ingress -o jsonpath='{{.metadata.name}}'");
                ingressName.Trim('\'').ShouldBe("web-ingress");
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
    public async Task FullPipeline_DeployIngress_VariableSubstitution(string communicationStyle)
    {
        var testNs = $"squid-ingvar-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var serverTaskId = await SeedIngressAsync(testNs, communicationStyle);

            await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

            ExecutionCapture.CapturedRequests.ShouldNotBeEmpty();

            var capturedRequest = ExecutionCapture.CapturedRequests[0];
            var yamlFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in capturedRequest.Files)
            {
                if (file.Key.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                    yamlFiles[file.Key] = file.Value;
            }

            yamlFiles.ShouldContainKey("ingress.yaml");

            var ingressYaml = Encoding.UTF8.GetString(yamlFiles["ingress.yaml"]);
            ingressYaml.ShouldNotContain("#{");
            ingressYaml.ShouldContain(testNs);

            // Apply to cluster to verify valid YAML
            var tempDir = await WriteYamlToTempDirAsync(yamlFiles);

            try
            {
                await _cluster.KubectlAsync($"apply -f \"{tempDir}\" --namespace={testNs}");
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

    [Fact]
    public async Task FullPipeline_DeployIngress_NoRules_SkipsAction()
    {
        var testNs = $"squid-ingnr-{Guid.NewGuid().ToString("N")[..8]}";

        var serverTaskId = await SeedIngressAsync(testNs, "KubernetesApi", includeRules: false);

        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        ExecutionCapture.CapturedRequests.ShouldBeEmpty("Handler returned null — pipeline should skip this action");
    }

    // ========================================================================
    // Seeders
    // ========================================================================

    private async Task<int> SeedIngressAsync(string namespaceName, string communicationStyle, bool includeRules = true)
    {
        ExecutionCapture.Clear();

        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateVariablesAsync(variableSet.Id,
                ("Namespace", namespaceName, VariableType.String, false)).ConfigureAwait(false);

            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Ingress").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Apply Ingress",
                actionType: "Squid.KubernetesDeployIngress").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);

            var properties = new List<(string Name, string Value)>
            {
                ("Squid.Action.KubernetesContainers.IngressName", "web-ingress"),
                ("Squid.Action.Kubernetes.Namespace", "#{Namespace}"),
                ("Squid.Action.KubernetesContainers.IngressClassName", "nginx"),
                ("Squid.Action.KubernetesContainers.IngressAnnotations",
                    "{\"nginx.ingress.kubernetes.io/rewrite-target\": \"/\"}")
            };

            if (includeRules)
            {
                properties.Add(("Squid.Action.KubernetesContainers.IngressRules",
                    "[{\"host\":\"app.example.com\",\"http\":{\"paths\":[{\"path\":\"/\",\"pathType\":\"Prefix\",\"backend\":{\"service\":{\"name\":\"web-svc\",\"port\":{\"number\":80}}}}]}}]"));
                properties.Add(("Squid.Action.KubernetesContainers.IngressTlsCertificates",
                    "[{\"hosts\":[\"app.example.com\"],\"secretName\":\"tls-secret\"}]"));
            }

            await builder.CreateActionPropertiesAsync(action.Id, properties.ToArray()).ConfigureAwait(false);

            serverTaskId = await CreateDeploymentInfrastructureAsync(
                builder, repository, unitOfWork, project, communicationStyle).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private static async Task<int> CreateDeploymentInfrastructureAsync(
        TestDataBuilder builder, IRepository repository, IUnitOfWork unitOfWork, Project project, string communicationStyle, CancellationToken ct = default)
    {
        var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
        var environment = await builder.CreateEnvironmentAsync("E2E Ingress Environment").ConfigureAwait(false);

        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = communicationStyle,
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
            Name = "E2E Ingress Target",
            IsDisabled = false,
            Roles = "k8s",
            EnvironmentIds = environment.Id.ToString(),
            Json = "{\"Endpoint\":{\"Uri\":\"https://localhost:10933\",\"Thumbprint\":\"E2E-THUMBPRINT\"}}",
            Thumbprint = "E2E-THUMBPRINT",
            Uri = "https://localhost:10933",
            HasLatestCalamari = false,
            Endpoint = endpointJson,
            DataVersion = Array.Empty<byte>(),
            SpaceId = 1,
            OperatingSystem = OperatingSystemType.Windows,
            ShellName = "PowerShell",
            ShellVersion = "7.0",
            LicenseHash = string.Empty,
            Slug = "e2e-ingress-target"
        };

        await repository.InsertAsync(machine, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var account = new DeploymentAccount
        {
            SpaceId = 1,
            Name = "E2E Ingress Account",
            Slug = "e2e-ingress-account",
            AccountType = AccountType.Token,
            Credentials = DeploymentAccountCredentialsConverter.Serialize(
                new TokenCredentials { Token = "e2e-test-token" })
        };

        await repository.InsertAsync(account, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

        var deployment = new Deployment
        {
            Name = "E2E Ingress Deployment",
            SpaceId = 1,
            ChannelId = channel.Id,
            ProjectId = project.Id,
            ReleaseId = release.Id,
            EnvironmentId = environment.Id,
            DeployedBy = 1,
            Created = DateTimeOffset.UtcNow,
            Json = string.Empty
        };

        await repository.InsertAsync(deployment, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var serverTask = new ServerTask
        {
            Name = "E2E Ingress Task",
            Description = "E2E ingress deployment test",
            QueueTime = DateTimeOffset.UtcNow,
            State = TaskState.Pending,
            ServerTaskType = "Deploy",
            ProjectId = project.Id,
            EnvironmentId = environment.Id,
            SpaceId = 1,
            LastModified = DateTimeOffset.UtcNow,
            BusinessProcessState = "Queued",
            StateOrder = 1,
            Weight = 1,
            BatchId = 0,
            JSON = string.Empty,
            HasWarningsOrErrors = false,
            ServerNodeId = Guid.NewGuid(),
            DurationSeconds = 0,
            DataVersion = Array.Empty<byte>()
        };

        await repository.InsertAsync(serverTask, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        deployment.TaskId = serverTask.Id;
        await repository.UpdateAsync(deployment, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        return serverTask.Id;
    }

    // ========================================================================
    // Assertions & Helpers
    // ========================================================================

    private async Task AssertTaskSuccessAsync()
    {
        await _fixture.Run<IServerTaskDataProvider>(async taskDataProvider =>
        {
            var tasks = await taskDataProvider.GetAllServerTasksAsync(CancellationToken.None).ConfigureAwait(false);

            tasks.ShouldNotBeNull();
            tasks.Count.ShouldBeGreaterThanOrEqualTo(1);

            var task = tasks.First(t => t.State == TaskState.Success);
            task.ShouldNotBeNull("Expected at least one task in Success state");
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
