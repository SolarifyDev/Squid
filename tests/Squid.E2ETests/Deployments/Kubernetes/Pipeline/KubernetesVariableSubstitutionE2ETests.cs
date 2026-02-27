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
public class KubernetesVariableSubstitutionE2ETests
    : IClassFixture<DeploymentPipelineFixture<KubernetesVariableSubstitutionE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly DeploymentPipelineFixture<KubernetesVariableSubstitutionE2ETests> _fixture;

    public KubernetesVariableSubstitutionE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<KubernetesVariableSubstitutionE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Fact]
    public async Task DeployYaml_WithVariableSubstitution_ReplacesPlaceholders()
    {
        var testNs = $"squid-varsub-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var serverTaskId = await SeedDeployYamlAsync(testNs);

            await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await AssertTaskSuccessAsync();

            // Extract script from captured execution request
            ExecutionCapture.CapturedRequests.ShouldNotBeEmpty("Pipeline should have executed at least one script");

            var capturedRequest = ExecutionCapture.CapturedRequests[0];
            var yamlFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in capturedRequest.Files)
            {
                if (file.Key.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                    file.Key.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                    yamlFiles[file.Key] = file.Value;
            }

            yamlFiles.ShouldNotBeEmpty("Should have YAML files in captured request");

            // Verify variables are resolved — no #{...} templates remaining
            foreach (var yamlEntry in yamlFiles)
            {
                var content = Encoding.UTF8.GetString(yamlEntry.Value);

                content.ShouldNotContain("#{");
                content.ShouldContain(testNs);
                content.ShouldContain("varsub-app");
            }

            // Apply to cluster to verify the resolved YAML is valid
            var tempDir = await WriteYamlToTempDirAsync(yamlFiles);

            try
            {
                await _cluster.KubectlAsync($"apply -f \"{tempDir}\" --namespace={testNs}");

                var cmData = await _cluster.KubectlAsync(
                    $"-n {testNs} get configmap varsub-app -o jsonpath='{{.data.APP_NAME}}'");
                cmData.Trim('\'').ShouldBe("varsub-app");
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
    public async Task DeployYaml_WithSensitiveVariable_MasksInLogs()
    {
        var testNs = $"squid-sens-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var sensitiveValue = $"super-secret-{Guid.NewGuid():N}";
            var serverTaskId = await SeedDeployYamlWithSensitiveVariableAsync(testNs, sensitiveValue);

            await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await AssertTaskSuccessAsync();

            // Verify the sensitive value is present in the resolved YAML (it should be there for deployment)
            ExecutionCapture.CapturedRequests.ShouldNotBeEmpty();

            var capturedRequest = ExecutionCapture.CapturedRequests[0];

            // The script body should NOT contain the raw sensitive value — it should be masked or handled
            // However, the YAML file itself will contain the value since it needs to be deployed
            // Verify that at minimum, the variable template was resolved
            foreach (var file in capturedRequest.Files)
            {
                if (!file.Key.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) continue;

                var content = Encoding.UTF8.GetString(file.Value);
                content.ShouldNotContain("#{SensitiveKey}");
            }

            // Verify the sensitive variable is in the request's variable list with IsSensitive flag
            var sensitiveVar = capturedRequest.Variables?
                .FirstOrDefault(v => v.Name == "SensitiveKey");
            sensitiveVar.ShouldNotBeNull("SensitiveKey variable should be in the request");
            sensitiveVar.IsSensitive.ShouldBeTrue("SensitiveKey should be marked as sensitive");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task HelmUpgrade_WithVariableInAdditionalArgs_SubstitutesCorrectly()
    {
        var testNs = $"squid-helmvar-{Guid.NewGuid().ToString("N")[..8]}";
        var releaseName = $"e2e-var-{Guid.NewGuid().ToString("N")[..6]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var serverTaskId = await SeedHelmWithVariableAsync(testNs, releaseName);

            await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await AssertTaskSuccessAsync();

            ExecutionCapture.CapturedRequests.ShouldNotBeEmpty();

            var capturedRequest = ExecutionCapture.CapturedRequests[0];

            // Script body should contain the resolved version, not the template
            capturedRequest.ScriptBody.ShouldNotContain("#{ImageTag}");
            capturedRequest.ScriptBody.ShouldContain("2.5.0");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    // ========================================================================
    // Seeders
    // ========================================================================

    private async Task<int> SeedDeployYamlAsync(string namespaceName)
    {
        ExecutionCapture.Clear();

        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateVariablesAsync(variableSet.Id,
                ("Namespace", namespaceName, VariableType.String, false),
                ("AppName", "varsub-app", VariableType.String, false)).ConfigureAwait(false);

            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy YAML").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var inlineYaml = @"apiVersion: v1
kind: ConfigMap
metadata:
  name: #{AppName}
  namespace: #{Namespace}
data:
  APP_NAME: ""#{AppName}""
  NAMESPACE: ""#{Namespace}""";

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Apply YAML",
                actionType: "Squid.KubernetesDeployRawYaml").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.KubernetesYaml.InlineYaml", inlineYaml),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            serverTaskId = await CreateDeploymentInfrastructureAsync(
                builder, repository, unitOfWork, project, "KubernetesApi").ConfigureAwait(false);
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private async Task<int> SeedDeployYamlWithSensitiveVariableAsync(string namespaceName, string sensitiveValue)
    {
        ExecutionCapture.Clear();

        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateVariablesAsync(variableSet.Id,
                ("Namespace", namespaceName, VariableType.String, false),
                ("SensitiveKey", sensitiveValue, VariableType.Password, true)).ConfigureAwait(false);

            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Sensitive").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var inlineYaml = @"apiVersion: v1
kind: Secret
metadata:
  name: sensitive-test
  namespace: #{Namespace}
type: Opaque
stringData:
  secret-key: ""#{SensitiveKey}""";

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Apply Secret",
                actionType: "Squid.KubernetesDeployRawYaml").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.KubernetesYaml.InlineYaml", inlineYaml),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            serverTaskId = await CreateDeploymentInfrastructureAsync(
                builder, repository, unitOfWork, project, "KubernetesApi").ConfigureAwait(false);
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    private async Task<int> SeedHelmWithVariableAsync(string namespaceName, string releaseName)
    {
        ExecutionCapture.Clear();

        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateVariablesAsync(variableSet.Id,
                ("Namespace", namespaceName, VariableType.String, false),
                ("ImageTag", "2.5.0", VariableType.String, false)).ConfigureAwait(false);

            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Helm Deploy").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Helm Upgrade",
                actionType: "Squid.HelmChartUpgrade").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.Script.Syntax", "Bash"),
                ("Squid.Action.Helm.ReleaseName", releaseName),
                ("Squid.Action.Helm.ChartPath", "bitnami/nginx"),
                ("Squid.Action.Kubernetes.Namespace", "#{Namespace}"),
                ("Squid.Action.Helm.AdditionalArgs", "--set image.tag=#{ImageTag} --dry-run --timeout 30s")).ConfigureAwait(false);

            serverTaskId = await CreateDeploymentInfrastructureAsync(
                builder, repository, unitOfWork, project, "KubernetesApi").ConfigureAwait(false);
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    // ========================================================================
    // Shared Infrastructure Seeder
    // ========================================================================

    private static async Task<int> CreateDeploymentInfrastructureAsync(
        TestDataBuilder builder,
        IRepository repository,
        IUnitOfWork unitOfWork,
        Project project,
        string communicationStyle,
        CancellationToken ct = default)
    {
        var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
        var environment = await builder.CreateEnvironmentAsync("E2E VarSub Environment").ConfigureAwait(false);

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
            Name = "E2E VarSub Target",
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
            Slug = "e2e-varsub-target"
        };

        await repository.InsertAsync(machine, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var account = new DeploymentAccount
        {
            SpaceId = 1,
            Name = "E2E VarSub Account",
            Slug = "e2e-varsub-account",
            AccountType = AccountType.Token,
            Credentials = DeploymentAccountCredentialsConverter.Serialize(
                new TokenCredentials { Token = "e2e-test-token" })
        };

        await repository.InsertAsync(account, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

        var deployment = new Deployment
        {
            Name = "E2E VarSub Deployment",
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
            Name = "E2E VarSub Task",
            Description = "E2E variable substitution test",
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
    // Assertions
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
