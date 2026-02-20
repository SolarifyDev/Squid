using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Agent;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class KubernetesAgentE2ETests
    : IClassFixture<KubernetesAgentE2EFixture<KubernetesAgentE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly KubernetesAgentE2EFixture<KubernetesAgentE2ETests> _fixture;

    public KubernetesAgentE2ETests(
        KindClusterFixture cluster,
        KubernetesAgentE2EFixture<KubernetesAgentE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    [Fact]
    public async Task Agent_RunScript_EchoOutput_Success()
    {
        var serverTaskId = await SeedRunScriptAsync("echo 'hello-from-agent'");

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskStateAsync(serverTaskId, TaskState.Success);
        _fixture.LogSink.ContainsMessage("hello-from-agent").ShouldBeTrue(
            "Expected script output 'hello-from-agent' in logs");
    }

    [Fact]
    public async Task Agent_RunScript_NonZeroExit_Failure()
    {
        var serverTaskId = await SeedRunScriptAsync("exit 42");

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskStateAsync(serverTaskId, TaskState.Failed);
    }

    [Fact]
    public async Task Agent_KubectlGetPods_ReturnsClusterData()
    {
        var serverTaskId = await SeedRunScriptAsync(
            "kubectl get pods -n kube-system --no-headers");

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskStateAsync(serverTaskId, TaskState.Success);
        _fixture.LogSink.ContainsMessage("kube-system").ShouldBeTrue(
            "Expected kube-system pod data in logs");
    }

    [Fact]
    public async Task Agent_DeployYaml_ConfigMapApplied()
    {
        var testNs = $"squid-e2e-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var script = $$"""
                cat <<'YAML' | kubectl apply -f -
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: agent-e2e-config
                  namespace: {{testNs}}
                data:
                  test-key: agent-test-value
                YAML
                kubectl get configmap agent-e2e-config -n {{testNs}} -o jsonpath='{.data.test-key}'
                """;

            var serverTaskId = await SeedRunScriptAsync(script);

            await ExecutePipelineAsync(serverTaskId);

            await AssertTaskStateAsync(serverTaskId, TaskState.Success);

            var cmValue = await _cluster.KubectlAsync(
                $"-n {testNs} get configmap agent-e2e-config -o jsonpath='{{.data.test-key}}'");
            cmValue.Trim('\'').ShouldBe("agent-test-value");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task Agent_FileTransfer_YamlAppliedViaHalibut()
    {
        var testNs = $"squid-e2e-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var inlineYaml = $"""
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: agent-file-transfer-config
                  namespace: {testNs}
                data:
                  transferred-key: halibut-datastream-value
                """;

            var serverTaskId = await SeedDeployYamlAsync(inlineYaml);

            await ExecutePipelineAsync(serverTaskId);

            await AssertTaskStateAsync(serverTaskId, TaskState.Success);

            var cmValue = await _cluster.KubectlAsync(
                $"-n {testNs} get configmap agent-file-transfer-config -o jsonpath='{{.data.transferred-key}}'");
            cmValue.Trim('\'').ShouldBe("halibut-datastream-value");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    // ========================================================================
    // Seed Helpers
    // ========================================================================

    private async Task<int> SeedRunScriptAsync(string scriptBody)
    {
        _fixture.LogSink.Clear();

        return await SeedDeploymentAsync(
            "Squid.KubernetesRunScript",
            ("Squid.Action.Script.ScriptBody", scriptBody),
            ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);
    }

    private async Task<int> SeedDeployYamlAsync(string inlineYaml)
    {
        _fixture.LogSink.Clear();

        return await SeedDeploymentAsync(
            "Squid.KubernetesDeployRawYaml",
            ("Squid.Action.KubernetesYaml.InlineYaml", inlineYaml),
            ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);
    }

    private async Task<int> SeedDeploymentAsync(
        string actionType,
        params (string Name, string Value)[] actionProperties)
    {
        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(
                process.Id, 1, "Agent Step").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Agent action",
                actionType: actionType).ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id, actionProperties).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("E2E Agent Environment").ConfigureAwait(false);

            var machine = CreateAgentMachine(environment,
                _fixture.Stub.SubscriptionId, _fixture.Stub.Thumbprint);
            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(
                project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "Agent E2E Deployment",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = environment.Id,
                DeployedBy = 1,
                Created = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = "Agent E2E Task",
                Description = "Agent E2E test task",
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

            await repository.InsertAsync(serverTask).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            serverTaskId = serverTask.Id;
        }).ConfigureAwait(false);

        return serverTaskId;
    }

    // ========================================================================
    // Assertion + Execution Helpers
    // ========================================================================

    private static Machine CreateAgentMachine(
        Environment environment, string subscriptionId, string thumbprint)
    {
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesAgent",
            SubscriptionId = subscriptionId,
            Thumbprint = thumbprint,
            Namespace = "default"
        });

        return new Machine
        {
            Name = "E2E K8s Agent",
            IsDisabled = false,
            Roles = "k8s",
            EnvironmentIds = environment.Id.ToString(),
            Json = string.Empty,
            Thumbprint = thumbprint,
            Uri = string.Empty,
            HasLatestCalamari = false,
            Endpoint = endpointJson,
            DataVersion = Array.Empty<byte>(),
            SpaceId = 1,
            OperatingSystem = OperatingSystemType.Linux,
            ShellName = "Bash",
            ShellVersion = string.Empty,
            PollingSubscriptionId = subscriptionId,
            LicenseHash = string.Empty,
            Slug = $"e2e-k8s-agent-{subscriptionId[..8]}"
        };
    }

    private async Task ExecutePipelineAsync(int serverTaskId)
    {
        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task AssertTaskStateAsync(int serverTaskId, string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async taskDataProvider =>
        {
            var task = await taskDataProvider.GetServerTaskByIdAsync(
                serverTaskId, CancellationToken.None).ConfigureAwait(false);

            task.ShouldNotBeNull($"ServerTask {serverTaskId} not found");
            task.State.ShouldBe(expectedState,
                $"Expected task state '{expectedState}' but was '{task.State}'");
        }).ConfigureAwait(false);
    }
}
