using System.Linq.Expressions;
using System.Net;
using Autofac;
using Mediator.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Runbooks;
using Squid.Core.Services.Deployments.Runbooks;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.IntegrationTests.Base;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Runbook;
using Squid.Message.Constants;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Runbook;
using Xunit;

namespace Squid.E2ETests.Deployments.Tentacle;

[Trait("Category", "E2E")]
public class RunbookExecutionE2ETests(TentacleListeningE2EFixture<RunbookExecutionE2ETests> fixture)
    : IClassFixture<TentacleListeningE2EFixture<RunbookExecutionE2ETests>>
{
    [Fact]
    public async Task RunNow_ExecutesPinnedVersionAndPersistsResultsWithoutDeployment()
    {
        const string versionAOutput = "runbook-version-a";
        const string versionBOutput = "runbook-version-b";
        const string versionBError = "runbook-version-b-error";

        var project = await CreateProjectAsync();
        var runbook = await fixture.Run<IRunbookService, RunbookDto>(service =>
            service.CreateRunbookAsync(new CreateRunbookCommand
            {
                Runbook = new CreateRunbookModel
                {
                    Name = "Runbook execution E2E",
                    ProjectId = project.Id,
                    SpaceId = project.SpaceId
                }
            }));
        var actionId = await SeedScriptAsync(runbook.ProcessId, $"echo '{versionAOutput}'");
        var versionA = await PublishAsync(runbook);

        // Run Now must use the published script even if the draft has changed.
        await fixture.Run<SquidDbContext>(db => db.Set<DeploymentActionProperty>()
            .Where(p => p.ActionId == actionId && p.PropertyName == SpecialVariables.Action.ScriptBody)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.PropertyValue,
                $"echo '{versionBOutput}'; echo '{versionBError}' >&2; exit 1")));
        var (runA, jobA) = await QueueAsync(runbook);

        var versionB = await PublishAsync(runbook);
        versionB.Id.ShouldNotBe(versionA.Id);
        await DeliverAsync(jobA);

        var (runB, jobB) = await QueueAsync(runbook);
        await DeliverAsync(jobB);

        runA.SnapshotId.ShouldBe(versionA.Id);
        runA.SnapshotVersion.ShouldBe(versionA.Version);
        runB.SnapshotId.ShouldBe(versionB.Id);
        runB.SnapshotVersion.ShouldBe(versionB.Version);
        runB.TaskId.ShouldNotBe(runA.TaskId);

        await fixture.Run<IServerTaskService>(async tasks =>
        {
            var taskA = await tasks.GetTaskAsync(runA.TaskId);
            taskA.State.ShouldBe(TaskState.Success);
            taskA.CompletedTime.ShouldNotBeNull();

            var logsA = await tasks.GetTaskLogsAsync(runA.TaskId);
            logsA.Items.ShouldContain(log => log.MessageText.Contains(versionAOutput));
            logsA.Items.ShouldNotContain(log => log.MessageText.Contains(versionBOutput));

            var taskB = await tasks.GetTaskAsync(runB.TaskId);
            taskB.State.ShouldBe(TaskState.Failed);
            taskB.CompletedTime.ShouldNotBeNull();
            taskB.ErrorMessage.ShouldContain(versionBError);

            var logsB = await tasks.GetTaskLogsAsync(runB.TaskId);
            logsB.Items.ShouldContain(log => log.MessageText.Contains(versionBOutput));
            logsB.Items.ShouldContain(log => log.Category == ServerTaskLogCategory.Error
                && log.MessageText.Contains(versionBError));
            logsB.Items.ShouldNotContain(log => log.MessageText.Contains(versionAOutput));
        });

        await fixture.Run<SquidDbContext>(async db =>
        {
            (await db.Set<Release>().AnyAsync(release => release.ProjectId == project.Id)).ShouldBeFalse();
            (await db.Set<Deployment>().AnyAsync(deployment => deployment.ProjectId == project.Id)).ShouldBeFalse();
        });
    }

    private async Task<Project> CreateProjectAsync()
    {
        Project project = null;
        await fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var data = new TestDataBuilder(repository, unitOfWork);
            var variables = await data.CreateVariableSetAsync();
            project = await data.CreateProjectAsync(variables.Id);
            await data.UpdateVariableSetOwnerAsync(variables, project.Id);
        });
        return project;
    }

    private async Task<int> SeedScriptAsync(int processId, string script)
    {
        var actionId = 0;
        await fixture.Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var data = new TestDataBuilder(repository, unitOfWork);
            var step = await data.CreateDeploymentStepAsync(processId, 1, "Run script");
            await data.CreateStepPropertiesAsync(step.Id, (SpecialVariables.Step.TargetRoles, "linux-server"));
            var action = await data.CreateDeploymentActionAsync(step.Id, 1, "Inline Bash");
            await data.CreateActionPropertiesAsync(action.Id,
                (SpecialVariables.Action.ScriptSource, "Inline"),
                (SpecialVariables.Action.ScriptSyntax, "Bash"),
                (SpecialVariables.Action.ScriptBody, script));
            actionId = action.Id;
        });
        return actionId;
    }

    private Task<RunbookPublishedSnapshotDto> PublishAsync(RunbookDto runbook) =>
        fixture.Run<IRunbookService, RunbookPublishedSnapshotDto>(service =>
            service.PublishRunbookAsync(new PublishRunbookCommand
            {
                Id = runbook.Id,
                ProjectId = runbook.ProjectId,
                SpaceId = runbook.SpaceId
            }));

    private async Task<(RunbookRunDto Run, Expression<Func<IRunbookTaskExecutor, Task>> Job)> QueueAsync(RunbookDto runbook)
    {
        var jobs = new MockSquidBackgroundJobClient { AsyncJobId = Guid.NewGuid().ToString("N") };
        var response = await fixture.Run<IMediator, RunRunbookResponse>(mediator =>
            mediator.SendAsync<RunRunbookCommand, RunRunbookResponse>(new RunRunbookCommand
            {
                Id = runbook.Id,
                ProjectId = runbook.ProjectId,
                SpaceId = runbook.SpaceId,
                EnvironmentId = fixture.EnvironmentId
            }), builder =>
            {
                builder.RegisterType<HttpContextAccessor>().As<IHttpContextAccessor>();
                builder.RegisterInstance(jobs).As<ISquidBackgroundJobClient>();
            });

        response.Code.ShouldBe(HttpStatusCode.Accepted);
        response.Data.State.ShouldBe(TaskState.Pending);
        response.Data.EnvironmentId.ShouldBe(fixture.EnvironmentId);
        var job = (Expression<Func<IRunbookTaskExecutor, Task>>)jobs.EnqueuedJobs.Single();
        return (response.Data, job);
    }

    private Task DeliverAsync(Expression<Func<IRunbookTaskExecutor, Task>> job) =>
        fixture.Run<IRunbookTaskExecutor>(job.Compile());
}
