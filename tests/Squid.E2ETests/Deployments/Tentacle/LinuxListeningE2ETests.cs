using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Shouldly;
using Xunit;

namespace Squid.E2ETests.Deployments.Tentacle;

[Trait("Category", "E2E")]
public class LinuxListeningE2ETests
    : IClassFixture<LinuxListeningE2EFixture<LinuxListeningE2ETests>>
{
    private readonly LinuxListeningE2EFixture<LinuxListeningE2ETests> _fixture;

    public LinuxListeningE2ETests(LinuxListeningE2EFixture<LinuxListeningE2ETests> fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Listening_EchoScript_Success()
    {
        var serverTaskId = await SeedRunScriptAsync("echo 'hello-from-linux-listening'");

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskStateAsync(serverTaskId, TaskState.Success);
        _fixture.LogSink.ContainsMessage("hello-from-linux-listening").ShouldBeTrue(
            "Expected script output in logs");
    }

    [Fact]
    public async Task Listening_NonZeroExitCode_TaskFails()
    {
        var serverTaskId = await SeedRunScriptAsync("exit 1");

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskStateAsync(serverTaskId, TaskState.Failed);
    }

    [Fact]
    public async Task Listening_MultiLineOutput_AllCaptured()
    {
        var script = """
            echo 'listen-one'
            echo 'listen-two'
            echo 'listen-three'
            """;

        var serverTaskId = await SeedRunScriptAsync(script);

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskStateAsync(serverTaskId, TaskState.Success);
        _fixture.LogSink.ContainsMessage("listen-one").ShouldBeTrue();
        _fixture.LogSink.ContainsMessage("listen-two").ShouldBeTrue();
        _fixture.LogSink.ContainsMessage("listen-three").ShouldBeTrue();
    }

    [Fact]
    public async Task Listening_StderrOutput_CapturedInLogs()
    {
        var serverTaskId = await SeedRunScriptAsync("echo 'listening-stderr' >&2");

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskStateAsync(serverTaskId, TaskState.Success);
        _fixture.LogSink.ContainsMessage("listening-stderr").ShouldBeTrue();
    }

    // ========================================================================
    // Seeder
    // ========================================================================

    private async Task<int> SeedRunScriptAsync(string scriptBody)
    {
        _fixture.LogSink.Clear();

        var serverTaskId = 0;

        await _fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Linux Listening Step").ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id, ("Squid.Action.TargetRoles", "linux-server")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Run Script", actionType: "Squid.Script").ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action.Id, "linux-server").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.Script.ScriptBody", scriptBody),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "Linux Listening Deployment",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = _fixture.EnvironmentId,
                DeployedBy = 1,
                CreatedDate = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = "Linux Listening Task",
                Description = "Linux Tentacle Listening E2E",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = project.Id,
                EnvironmentId = _fixture.EnvironmentId,
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
    // Execution + Assertion
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
                // Controlled script failure
            }
        }).ConfigureAwait(false);
    }

    private async Task AssertTaskStateAsync(int serverTaskId, string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);

            task.ShouldNotBeNull($"ServerTask {serverTaskId} not found");
            task.State.ShouldBe(expectedState, $"Expected '{expectedState}' but got '{task.State}'");
        }).ConfigureAwait(false);
    }
}
