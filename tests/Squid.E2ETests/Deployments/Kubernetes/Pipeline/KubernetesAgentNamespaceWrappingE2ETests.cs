using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Shouldly;
using Xunit;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using Squid.Core.Services.DeploymentExecution.Filtering;
namespace Squid.E2ETests.Deployments.Kubernetes.Pipeline;

[Collection("KindCluster")]
[Trait("Category", "E2E")]
public class KubernetesAgentNamespaceWrappingE2ETests
    : IClassFixture<DeploymentPipelineFixture<KubernetesAgentNamespaceWrappingE2ETests>>
{
    private readonly DeploymentPipelineFixture<KubernetesAgentNamespaceWrappingE2ETests> _fixture;

    public KubernetesAgentNamespaceWrappingE2ETests(
        KindClusterFixture cluster,
        DeploymentPipelineFixture<KubernetesAgentNamespaceWrappingE2ETests> fixture)
    {
        _fixture = fixture;
    }

    private CapturingExecutionStrategy ExecutionCapture => _fixture.ExecutionCapture;

    [Fact]
    public async Task Agent_RunScript_CapturedScriptContainsNamespaceContext()
    {
        var serverTaskId = await SeedRunScriptForAgentAsync(
            "kubectl get pods", "custom-namespace");

        await ExecutePipelineAsync(serverTaskId);

        ExecutionCapture.CapturedRequests.ShouldNotBeEmpty();

        var captured = ExecutionCapture.CapturedRequests[0];
        captured.ScriptBody.ShouldContain(
            "kubectl config set-context --current --namespace=\"custom-namespace\"");
        captured.ScriptBody.ShouldContain("kubectl get pods");
    }

    [Fact]
    public async Task Agent_DeployRawYaml_CapturedScriptContainsNamespaceContext()
    {
        var inlineYaml = @"apiVersion: v1
kind: ConfigMap
metadata:
  name: test-config
data:
  key: value";

        var serverTaskId = await SeedDeployYamlForAgentAsync(inlineYaml, "deploy-ns");

        await ExecutePipelineAsync(serverTaskId);

        ExecutionCapture.CapturedRequests.ShouldNotBeEmpty();

        var captured = ExecutionCapture.CapturedRequests[0];
        captured.ScriptBody.ShouldContain(
            "kubectl config set-context --current --namespace=\"deploy-ns\"");
        captured.ScriptBody.ShouldContain("kubectl apply -f");
    }

    [Fact]
    public async Task Agent_RunScript_DefaultNamespace_UsesDefault()
    {
        var serverTaskId = await SeedRunScriptForAgentAsync(
            "echo hello", null);

        await ExecutePipelineAsync(serverTaskId);

        ExecutionCapture.CapturedRequests.ShouldNotBeEmpty();

        var captured = ExecutionCapture.CapturedRequests[0];
        captured.ScriptBody.ShouldContain(
            "kubectl config set-context --current --namespace=\"default\"");
    }

    [Fact]
    public async Task Agent_RunScript_VariableTemplateNamespace_ExpandedCorrectly()
    {
        var serverTaskId = await SeedRunScriptWithVariableNamespaceAsync(
            "kubectl get pods", "#{TargetNamespace}", "resolved-ns");

        await ExecutePipelineAsync(serverTaskId);

        ExecutionCapture.CapturedRequests.ShouldNotBeEmpty();

        var captured = ExecutionCapture.CapturedRequests[0];
        captured.ScriptBody.ShouldContain(
            "kubectl config set-context --current --namespace=\"resolved-ns\"");
        captured.ScriptBody.ShouldNotContain("#{TargetNamespace}");
    }

    [Theory]
    [InlineData("KubernetesApi")]
    [InlineData("KubernetesAgent")]
    public async Task RunScript_BothStyles_OnlyAgentGetsNamespaceWrapper(string communicationStyle)
    {
        var serverTaskId = await SeedRunScriptAsync(
            "echo test", "test-ns", communicationStyle);

        await ExecutePipelineAsync(serverTaskId);

        ExecutionCapture.CapturedRequests.ShouldNotBeEmpty();

        var captured = ExecutionCapture.CapturedRequests[0];

        if (communicationStyle == "KubernetesAgent")
        {
            captured.ScriptBody.ShouldContain(
                "kubectl config set-context --current --namespace=");
        }
        else
        {
            // KubernetesApi uses KubernetesApiContextScriptBuilder which wraps differently
            // (full auth + namespace via KubectlContext.sh template)
            captured.ScriptBody.ShouldNotContain("kubectl config set-context --current --namespace=");
        }
    }

    // ========================================================================
    // Seeders
    // ========================================================================

    private Task<int> SeedRunScriptForAgentAsync(string scriptBody, string ns)
        => SeedRunScriptAsync(scriptBody, ns, "KubernetesAgent");

    private Task<int> SeedDeployYamlForAgentAsync(string inlineYaml, string ns)
    {
        var props = new List<(string, string)>
        {
            ("Squid.Action.KubernetesYaml.InlineYaml", inlineYaml),
            ("Squid.Action.Script.Syntax", "Bash")
        };

        if (ns != null)
            props.Add(("Squid.Action.KubernetesContainers.Namespace", ns));

        return SeedActionAsync("Squid.KubernetesDeployRawYaml", ns, "KubernetesAgent", props.ToArray());
    }

    private Task<int> SeedRunScriptAsync(string scriptBody, string ns, string communicationStyle)
    {
        var props = new List<(string, string)>
        {
            ("Squid.Action.Script.ScriptBody", scriptBody),
            ("Squid.Action.Script.Syntax", "Bash")
        };

        if (ns != null)
            props.Add(("Squid.Action.KubernetesContainers.Namespace", ns));

        return SeedActionAsync("Squid.KubernetesRunScript", ns, communicationStyle, props.ToArray());
    }

    private async Task<int> SeedRunScriptWithVariableNamespaceAsync(
        string scriptBody, string nsTemplate, string nsVariableValue)
    {
        ExecutionCapture.Clear();

        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            await builder.CreateVariablesAsync(variableSet.Id,
                ("TargetNamespace", nsVariableValue, Squid.Message.Enums.VariableType.String, false)).ConfigureAwait(false);

            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Test Step").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Test Action", actionType: "Squid.KubernetesRunScript").ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.Script.ScriptBody", scriptBody),
                ("Squid.Action.Script.Syntax", "Bash"),
                ("Squid.Action.KubernetesContainers.Namespace", nsTemplate)).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("Var Namespace Test Env").ConfigureAwait(false);

            var machine = CreateMachine(environment, "KubernetesAgent", "machine-ns");
            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "Var Namespace Test",
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
                Name = "Var Namespace Test Task",
                Description = "Test variable namespace expansion",
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

    private async Task<int> SeedActionAsync(
        string actionType, string ns, string communicationStyle,
        params (string Name, string Value)[] actionProperties)
    {
        ExecutionCapture.Clear();

        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Test Step").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id,
                ("Squid.Action.TargetRoles", "k8s")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(
                step.Id, 1, "Test Action", actionType: actionType).ConfigureAwait(false);

            await builder.CreateActionMachineRolesAsync(action.Id, "k8s").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id, actionProperties).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync("Namespace Wrap Test Env").ConfigureAwait(false);

            var machine = CreateMachine(environment, communicationStyle, ns);
            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            if (communicationStyle == "KubernetesApi")
            {
                var account = new DeploymentAccount
                {
                    SpaceId = 1,
                    Name = "Test Account",
                    Slug = $"test-account-{Guid.NewGuid():N}",
                    AccountType = AccountType.Token,
                    Credentials = DeploymentAccountCredentialsConverter.Serialize(
                        new TokenCredentials { Token = "test-token" })
                };

                await repository.InsertAsync(account).ConfigureAwait(false);
                await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
            }

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "Namespace Wrap Test",
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
                Name = "Namespace Wrap Test Task",
                Description = "Test namespace wrapping",
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
    // Helpers
    // ========================================================================

    private static Machine CreateMachine(Environment environment, string communicationStyle, string ns)
    {
        var endpointJson = communicationStyle == "KubernetesAgent"
            ? JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesAgent",
                Namespace = ns ?? "default"
            })
            : JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesApi",
                ClusterUrl = "https://localhost:6443",
                SkipTlsVerification = "True",
                Namespace = ns ?? "default",
                ResourceReferences = new[]
                {
                    new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = 1 }
                }
            });

        return new Machine
        {
            Name = "Namespace Wrap Test Target",
            IsDisabled = false,
            Roles = DeploymentTargetFinder.SerializeRoles(new[] { "k8s" }),
            EnvironmentIds = DeploymentTargetFinder.SerializeIds(new[] { environment.Id }),
            Endpoint = endpointJson,
            SpaceId = 1,
            Slug = $"ns-wrap-test-{Guid.NewGuid():N}"
        };
    }

    private async Task ExecutePipelineAsync(int serverTaskId)
    {
        await _fixture.Run<IDeploymentTaskExecutor>(async executor =>
        {
            await executor.ProcessAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
