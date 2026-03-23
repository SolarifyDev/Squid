using System.Text.Json;
using Autofac;
using Halibut;
using Halibut.Diagnostics;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Contracts.Tentacle;
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
        _fixture.LogSink.ContainsMessage("coredns").ShouldBeTrue(
            "Expected coredns pod in kube-system logs");
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
    public async Task Agent_RunScript_NamespaceWrapping_TargetsCorrectNamespace()
    {
        var testNs = $"squid-e2e-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            // Create a configmap in the target namespace first
            await _cluster.KubectlAsync(
                $"create configmap ns-marker --from-literal=marker=found -n {testNs}");

            // This RunScript doesn't specify namespace explicitly in the command —
            // it relies on the wrapper setting namespace context via kubectl config
            var serverTaskId = await SeedRunScriptWithNamespaceAsync(
                "kubectl get configmap ns-marker -o jsonpath='{.data.marker}'",
                testNs);

            await ExecutePipelineAsync(serverTaskId);

            await AssertTaskStateAsync(serverTaskId, TaskState.Success);
            _fixture.LogSink.ContainsMessage("found").ShouldBeTrue(
                "Expected 'found' from configmap in target namespace — " +
                "namespace wrapping should have set kubectl context to the correct namespace");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task Agent_DeployRawYaml_NamespaceWrapping_AppliesInCorrectNamespace()
    {
        var testNs = $"squid-e2e-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            // Inline YAML without namespace in metadata — relies on wrapper's namespace context
            var inlineYaml = """
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: no-ns-config
                data:
                  test-key: wrapper-applied
                """;

            var serverTaskId = await SeedDeployYamlWithNamespaceAsync(inlineYaml, testNs);

            await ExecutePipelineAsync(serverTaskId);

            await AssertTaskStateAsync(serverTaskId, TaskState.Success);

            // Verify the configmap was created in the target namespace (not default)
            var cmValue = await _cluster.KubectlAsync(
                $"-n {testNs} get configmap no-ns-config -o jsonpath='{{.data.test-key}}'");
            cmValue.Trim('\'').ShouldBe("wrapper-applied",
                "ConfigMap should be created in the wrapper-targeted namespace");
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

    [Fact]
    public async Task Agent_GetCapabilities_ReturnsExpectedServices()
    {
        var halibutRuntime = _fixture.LifetimeScope.Resolve<HalibutRuntime>();

        var endpoint = new ServiceEndPoint(
            $"poll://{_fixture.Stub.SubscriptionId}/",
            _fixture.Stub.Thumbprint,
            HalibutTimeoutsAndLimits.RecommendedValues());

        var client = halibutRuntime.CreateAsyncClient<ICapabilitiesService, IAsyncCapabilitiesService>(endpoint);

        var response = await client.GetCapabilitiesAsync(new CapabilitiesRequest());

        response.ShouldNotBeNull();
        response.SupportedServices.ShouldContain("IScriptService/v1");
        response.SupportedServices.ShouldContain("ICapabilitiesService/v1");
        response.AgentVersion.ShouldNotBeNullOrWhiteSpace();
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

    private async Task<int> SeedRunScriptWithNamespaceAsync(string scriptBody, string ns)
    {
        _fixture.LogSink.Clear();

        return await SeedDeploymentAsync(
            "Squid.KubernetesRunScript", ns,
            ("Squid.Action.Script.ScriptBody", scriptBody),
            ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);
    }

    private async Task<int> SeedDeployYamlWithNamespaceAsync(string inlineYaml, string ns)
    {
        _fixture.LogSink.Clear();

        return await SeedDeploymentAsync(
            "Squid.KubernetesDeployRawYaml", ns,
            ("Squid.Action.KubernetesYaml.InlineYaml", inlineYaml),
            ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);
    }

    private async Task<int> SeedDeploymentAsync(
        string actionType,
        params (string Name, string Value)[] actionProperties)
        => await SeedDeploymentAsync(actionType, "default", actionProperties).ConfigureAwait(false);

    private async Task<int> SeedDeploymentAsync(
        string actionType,
        string ns,
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
                _fixture.Stub.SubscriptionId, _fixture.Stub.Thumbprint, ns);
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
                CreatedDate = DateTimeOffset.UtcNow,
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
                LastModifiedDate = DateTimeOffset.UtcNow,
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
        Environment environment, string subscriptionId, string thumbprint, string ns = "default")
    {
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesAgent",
            SubscriptionId = subscriptionId,
            Thumbprint = thumbprint,
            Namespace = ns
        });

        return new Machine
        {
            Name = "E2E K8s Agent",
            IsDisabled = false,
            Roles = "k8s",
            EnvironmentIds = environment.Id.ToString(),
            Endpoint = endpointJson,
            DataVersion = Array.Empty<byte>(),
            SpaceId = 1,
            Slug = $"e2e-k8s-agent-{subscriptionId[..8]}"
        };
    }

    private async Task ExecutePipelineAsync(int serverTaskId)
    {
        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            try
            {
                await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (DeploymentScriptException)
            {
                // Controlled script failure — task state already recorded in DB
            }
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
