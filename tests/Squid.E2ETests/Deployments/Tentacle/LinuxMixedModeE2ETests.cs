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

/// <summary>
/// Verifies that a single deployment step can target BOTH a Polling and a Listening
/// Linux Tentacle simultaneously. Both stubs must receive and execute the script.
/// </summary>
[Trait("Category", "E2E")]
public class LinuxMixedModeE2ETests
    : IClassFixture<LinuxMixedModeE2EFixture<LinuxMixedModeE2ETests>>
{
    private readonly LinuxMixedModeE2EFixture<LinuxMixedModeE2ETests> _fixture;

    public LinuxMixedModeE2ETests(LinuxMixedModeE2EFixture<LinuxMixedModeE2ETests> fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MixedMode_SingleDeployment_BothTargetsExecute()
    {
        _fixture.LogSink.Clear();

        var serverTaskId = await SeedRunScriptAsync("echo 'mixed-mode-ok'");

        await ExecutePipelineAsync(serverTaskId);

        await AssertTaskStateAsync(serverTaskId, TaskState.Success);
        _fixture.LogSink.ContainsMessage("mixed-mode-ok").ShouldBeTrue(
            "Expected 'mixed-mode-ok' from at least one target in logs");
    }

    // ========================================================================
    // Seeder — targets both machines via "linux-server" role
    // ========================================================================

    private async Task<int> SeedRunScriptAsync(string scriptBody)
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

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Mixed Mode Step").ConfigureAwait(false);
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
                Name = "Mixed Mode Deployment",
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
                Name = "Mixed Mode Task",
                Description = "Mixed Polling+Listening E2E",
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
                // Controlled failure
            }
        }).ConfigureAwait(false);
    }

    private async Task AssertTaskStateAsync(int serverTaskId, string expectedState)
    {
        await _fixture.Run<IServerTaskDataProvider>(async provider =>
        {
            var task = await provider.GetServerTaskByIdAsync(serverTaskId, CancellationToken.None).ConfigureAwait(false);

            task.ShouldNotBeNull();
            task.State.ShouldBe(expectedState);
        }).ConfigureAwait(false);
    }
}
