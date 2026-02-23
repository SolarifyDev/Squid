using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Agents;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Agent;
using Squid.Message.Enums;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Kubernetes.Agent;

/// <summary>
/// E2E tests using real Squid.Tentacle components (TentacleCertificateManager, TentacleHalibutHost, LocalScriptService).
/// Verifies the full tentacle lifecycle: registration → Halibut polling → script execution on Kind cluster.
/// </summary>
[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class SquidTentacleE2ETests
    : IClassFixture<SquidTentacleE2EFixture<SquidTentacleE2ETests>>
{
    private readonly KindClusterFixture _cluster;
    private readonly SquidTentacleE2EFixture<SquidTentacleE2ETests> _fixture;

    public SquidTentacleE2ETests(
        KindClusterFixture cluster,
        SquidTentacleE2EFixture<SquidTentacleE2ETests> fixture)
    {
        _cluster = cluster;
        _fixture = fixture;
    }

    // ========================================================================
    // Registration Tests
    // ========================================================================

    [Fact]
    public async Task RealTentacle_Registration_CreatesMachineInDatabase()
    {
        await _fixture.Run<IAgentDataProvider>(async agentDataProvider =>
        {
            var machine = await agentDataProvider.GetAgentBySubscriptionIdAsync(
                _fixture.TentacleSubscriptionId, CancellationToken.None).ConfigureAwait(false);

            machine.ShouldNotBeNull();
            machine.Id.ShouldBe(_fixture.TentacleMachineId);
            machine.PollingSubscriptionId.ShouldBe(_fixture.TentacleSubscriptionId);
            machine.Thumbprint.ShouldBe(_fixture.TentacleThumbprint);
            machine.Roles.ShouldBe("k8s");
            machine.Endpoint.ShouldContain("KubernetesAgent");
            machine.Endpoint.ShouldContain(_fixture.TentacleSubscriptionId);
            machine.OperatingSystem.ShouldBe(OperatingSystemType.Linux);
            machine.ShellName.ShouldBe("Bash");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task RealTentacle_ReRegistration_UpdatesExistingMachine()
    {
        try
        {
            await _fixture.Run<IAgentService>(async agentService =>
            {
                var result = await agentService.RegisterAgentAsync(new RegisterAgentCommand
                {
                    MachineName = $"squid-tentacle-e2e-{_fixture.TentacleSubscriptionId[..8]}",
                    Thumbprint = _fixture.TentacleThumbprint,
                    SubscriptionId = _fixture.TentacleSubscriptionId,
                    Roles = "k8s,web",
                    EnvironmentIds = _fixture.TentacleEnvironmentId.ToString(),
                    Namespace = "production"
                }).ConfigureAwait(false);

                result.MachineId.ShouldBe(_fixture.TentacleMachineId);
            }).ConfigureAwait(false);

            await _fixture.Run<IAgentDataProvider>(async agentDataProvider =>
            {
                var machine = await agentDataProvider.GetAgentBySubscriptionIdAsync(
                    _fixture.TentacleSubscriptionId, CancellationToken.None).ConfigureAwait(false);

                machine.ShouldNotBeNull();
                machine.Id.ShouldBe(_fixture.TentacleMachineId);
                machine.Roles.ShouldBe("k8s,web");
                machine.Endpoint.ShouldContain("production");
            }).ConfigureAwait(false);
        }
        finally
        {
            // Restore original registration so other tests are not affected
            await _fixture.Run<IAgentService>(async agentService =>
            {
                await agentService.RegisterAgentAsync(new RegisterAgentCommand
                {
                    MachineName = $"squid-tentacle-e2e-{_fixture.TentacleSubscriptionId[..8]}",
                    Thumbprint = _fixture.TentacleThumbprint,
                    SubscriptionId = _fixture.TentacleSubscriptionId,
                    Roles = "k8s",
                    EnvironmentIds = _fixture.TentacleEnvironmentId.ToString(),
                    Namespace = "default"
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
    }

    // ========================================================================
    // Script Execution Tests (real Halibut → real LocalScriptService)
    // ========================================================================

    [Fact]
    public async Task RealTentacle_RunScript_EchoOutput_Success()
    {
        var serverTaskId = await SeedRunScriptAsync("echo 'hello-from-real-tentacle'");

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskStateAsync(serverTaskId, TaskState.Success);
        _fixture.LogSink.ContainsMessage("hello-from-real-tentacle").ShouldBeTrue(
            "Expected script output 'hello-from-real-tentacle' in logs");
    }

    [Fact]
    public async Task RealTentacle_RunScript_NonZeroExit_Failure()
    {
        var serverTaskId = await SeedRunScriptAsync("exit 42");

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskStateAsync(serverTaskId, TaskState.Failed);
    }

    [Fact]
    public async Task RealTentacle_KubectlGetPods_ReturnsClusterData()
    {
        var serverTaskId = await SeedRunScriptAsync(
            "kubectl get pods -n kube-system --no-headers");

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskStateAsync(serverTaskId, TaskState.Success);
        _fixture.LogSink.ContainsMessage("coredns").ShouldBeTrue(
            "Expected coredns pod in kube-system logs");
    }

    [Fact]
    public async Task RealTentacle_DeployYaml_ConfigMapApplied()
    {
        var testNs = $"squid-real-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var inlineYaml = $"""
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: real-tentacle-config
                  namespace: {testNs}
                data:
                  test-key: real-tentacle-value
                """;

            var serverTaskId = await SeedDeployYamlAsync(inlineYaml);

            await ExecutePipelineAsync(serverTaskId);

            await AssertTaskStateAsync(serverTaskId, TaskState.Success);

            var cmValue = await _cluster.KubectlAsync(
                $"-n {testNs} get configmap real-tentacle-config -o jsonpath='{{.data.test-key}}'");
            cmValue.Trim('\'').ShouldBe("real-tentacle-value");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task RealTentacle_NamespaceWrapping_TargetsCorrectNamespace()
    {
        var testNs = $"squid-real-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");
            await _cluster.KubectlAsync(
                $"create configmap ns-real-test --from-literal=marker=real-tentacle-found -n {testNs}");

            var serverTaskId = await SeedRunScriptWithCustomNamespaceMachineAsync(
                "kubectl get configmap ns-real-test -o jsonpath='{.data.marker}'",
                testNs);

            await ExecutePipelineAsync(serverTaskId);

            await AssertTaskStateAsync(serverTaskId, TaskState.Success);
            _fixture.LogSink.ContainsMessage("real-tentacle-found").ShouldBeTrue(
                "Expected 'real-tentacle-found' — namespace wrapping should target correct namespace via real tentacle");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    [Fact]
    public async Task RealTentacle_DeployYaml_NamespaceWrapping_AppliesInCorrectNamespace()
    {
        var testNs = $"squid-real-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            await _cluster.KubectlAsync($"create namespace {testNs}");

            var inlineYaml = """
                apiVersion: v1
                kind: ConfigMap
                metadata:
                  name: real-ns-config
                data:
                  test-key: real-wrapper-applied
                """;

            var serverTaskId = await SeedDeployYamlWithCustomNamespaceMachineAsync(inlineYaml, testNs);

            await ExecutePipelineAsync(serverTaskId);

            await AssertTaskStateAsync(serverTaskId, TaskState.Success);

            var cmValue = await _cluster.KubectlAsync(
                $"-n {testNs} get configmap real-ns-config -o jsonpath='{{.data.test-key}}'");
            cmValue.Trim('\'').ShouldBe("real-wrapper-applied");
        }
        finally
        {
            await _cluster.KubectlAsync($"delete namespace {testNs} --ignore-not-found");
        }
    }

    // ========================================================================
    // Seeders — Default Namespace (use fixture's registered machine)
    // ========================================================================

    private async Task<int> SeedRunScriptAsync(string scriptBody)
    {
        _fixture.LogSink.Clear();

        return await SeedDeploymentAsync(
            "Squid.KubernetesRunScript",
            _fixture.TentacleEnvironmentId,
            null,
            ("Squid.Action.Script.ScriptBody", scriptBody),
            ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);
    }

    private async Task<int> SeedDeployYamlAsync(string inlineYaml)
    {
        _fixture.LogSink.Clear();

        return await SeedDeploymentAsync(
            "Squid.KubernetesDeployRawYaml",
            _fixture.TentacleEnvironmentId,
            null,
            ("Squid.Action.KubernetesYaml.InlineYaml", inlineYaml),
            ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);
    }

    // ========================================================================
    // Seeders — Custom Namespace (create a new machine + environment)
    // ========================================================================

    private async Task<int> SeedRunScriptWithCustomNamespaceMachineAsync(string scriptBody, string ns)
    {
        _fixture.LogSink.Clear();

        return await SeedDeploymentWithCustomMachineAsync(
            "Squid.KubernetesRunScript", ns,
            ("Squid.Action.Script.ScriptBody", scriptBody),
            ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);
    }

    private async Task<int> SeedDeployYamlWithCustomNamespaceMachineAsync(string inlineYaml, string ns)
    {
        _fixture.LogSink.Clear();

        return await SeedDeploymentWithCustomMachineAsync(
            "Squid.KubernetesDeployRawYaml", ns,
            ("Squid.Action.KubernetesYaml.InlineYaml", inlineYaml),
            ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);
    }

    // ========================================================================
    // Core Seeder — Uses fixture's registered machine
    // ========================================================================

    private async Task<int> SeedDeploymentAsync(
        string actionType,
        int environmentId,
        Machine customMachine,
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
                process.Id, 1, "Real Tentacle Step").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Real Tentacle Action",
                actionType: actionType).ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id, actionProperties).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);

            if (customMachine != null)
            {
                var env = await builder.CreateEnvironmentAsync("Real Tentacle Custom NS Env").ConfigureAwait(false);
                customMachine.EnvironmentIds = env.Id.ToString();

                await repository.InsertAsync(customMachine).ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

                environmentId = env.Id;
            }

            var release = await builder.CreateReleaseAsync(
                project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "Real Tentacle Deployment",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = environmentId,
                DeployedBy = 1,
                Created = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = "Real Tentacle Task",
                Description = "Real tentacle E2E test task",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = project.Id,
                EnvironmentId = environmentId,
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
    // Core Seeder — Custom namespace machine (same tentacle subscription)
    // ========================================================================

    private async Task<int> SeedDeploymentWithCustomMachineAsync(
        string actionType,
        string ns,
        params (string Name, string Value)[] actionProperties)
    {
        var customMachine = CreateCustomNamespaceMachine(ns);

        return await SeedDeploymentAsync(actionType, 0, customMachine, actionProperties).ConfigureAwait(false);
    }

    private Machine CreateCustomNamespaceMachine(string ns)
    {
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesAgent",
            SubscriptionId = _fixture.TentacleSubscriptionId,
            Thumbprint = _fixture.TentacleThumbprint,
            Namespace = ns
        });

        return new Machine
        {
            Name = $"Real Tentacle Custom NS ({ns})",
            IsDisabled = false,
            Roles = "k8s",
            EnvironmentIds = string.Empty,
            Json = string.Empty,
            Thumbprint = _fixture.TentacleThumbprint,
            Uri = string.Empty,
            HasLatestCalamari = false,
            Endpoint = endpointJson,
            DataVersion = Array.Empty<byte>(),
            SpaceId = 1,
            OperatingSystem = OperatingSystemType.Linux,
            ShellName = "Bash",
            ShellVersion = string.Empty,
            PollingSubscriptionId = _fixture.TentacleSubscriptionId,
            LicenseHash = string.Empty,
            Slug = $"real-tentacle-ns-{Guid.NewGuid():N}"
        };
    }

    // ========================================================================
    // Execution + Assertion Helpers
    // ========================================================================

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
