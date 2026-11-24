using System.Text.Json;
using Autofac;
using Mediator.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Identity;
using Squid.Core.Services.ProjectTriggers;
using Squid.Core.Services.ProjectTriggers.Runtime;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.ProjectTriggers;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Requests.ProjectTriggers;
using Xunit;

namespace Squid.E2ETests.ProjectTriggers;

[Trait("Category", "E2E")]
public class ProjectTriggerE2ETests(ProjectTriggerE2EFixture fixture) : IClassFixture<ProjectTriggerE2EFixture>
{
    [Fact]
    public async Task ReplayedObservationAndDelivery_CreateOneReleaseAndDeployItOnceAsTheConfiguredUser()
    {
        var input = await SeedAsync();
        var triggerId = await CreateTriggerAsync(input);
        var initialDeliveries = fixture.Deliveries.Count;
        fixture.ExecutionCapture.Clear();

        (await Task.WhenAll(ObserveAsync(triggerId), ObserveAsync(triggerId))).Sum().ShouldBe(1);
        (await Task.WhenAll(ExecuteAsync(), ExecuteAsync())).Sum().ShouldBe(1);
        var created = await HistoryAsync(input, triggerId);
        created.Status.ShouldBe(ProjectTriggerRunStatuses.AwaitingDispatch, created.Reason);
        created.ExecutionUserId.ShouldBe(input.UserId);
        created.Revision.ShouldBe(1);
        fixture.Deliveries.Count.ShouldBe(initialDeliveries);

        await fixture.Run<IProjectTriggerWorker>(w => w.DispatchPendingAsync(10));
        var sent = await HistoryAsync(input, triggerId);
        sent.Status.ShouldBe(ProjectTriggerRunStatuses.Dispatched, sent.Reason);
        sent.TaskId.ShouldBe(created.TaskId);
        var delivery = fixture.Deliveries.Skip(initialDeliveries).Single();
        await fixture.Run<IDeploymentTaskExecutor>(delivery);
        await fixture.Run<IDeploymentTaskExecutor>(delivery);

        fixture.ExecutionCapture.CapturedRequests.Single().ScriptBody.ShouldContain("trigger-smoke");
        await fixture.Run<SquidDbContext>(async db =>
        {
            var release = await db.Set<Release>().SingleAsync(r => r.ProjectId == input.ProjectId);
            release.Id.ShouldBe(sent.ReleaseId.Value);
            release.CreatedBy.ShouldBe(input.UserId);
            release.ChannelId.ShouldBe(input.ChannelId);
            release.ProjectDeploymentProcessSnapshotId.ShouldBeGreaterThan(0);
            release.ProjectVariableSetSnapshotId.ShouldBeGreaterThan(0);
            var deployment = await db.Set<Deployment>().SingleAsync(d => d.ProjectId == input.ProjectId);
            deployment.Id.ShouldBe(sent.DeploymentId.Value);
            deployment.ReleaseId.ShouldBe(release.Id);
            deployment.DeployedBy.ShouldBe(input.UserId);
            deployment.TaskId.ShouldBe(sent.TaskId);
            var task = await db.Set<ServerTask>().SingleAsync(t => t.ProjectId == input.ProjectId);
            task.Id.ShouldBe(sent.TaskId.Value);
            task.State.ShouldBe(TaskState.Success);
        });
    }

    [Fact]
    public async Task ReplayedDelivery_WhilePaused_WaitsForExplicitResume()
    {
        var input = await SeedAsync();
        var triggerId = await CreateTriggerAsync(input);
        var initialDeliveries = fixture.Deliveries.Count;
        fixture.ExecutionCapture.Clear();
        await ObserveAsync(triggerId);
        await ExecuteAsync();
        await fixture.Run<IProjectTriggerWorker>(worker => worker.DispatchPendingAsync(10));
        var run = await HistoryAsync(input, triggerId);
        var taskId = run.TaskId.Value;
        var delivery = fixture.Deliveries.Skip(initialDeliveries).Single();
        await fixture.Run<IServerTaskDataProvider>(async tasks =>
        {
            await tasks.TransitionStateAsync(taskId, TaskState.Pending, TaskState.Executing);
            await tasks.TransitionStateAsync(taskId, TaskState.Executing, TaskState.Paused);
        });

        await fixture.Run<IDeploymentTaskExecutor>(delivery);

        fixture.ExecutionCapture.CapturedRequests.Count.ShouldBe(0);
        await fixture.Run<IServerTaskDataProvider>(async tasks =>
            (await tasks.GetServerTaskByIdNoTrackingAsync(taskId)).State.ShouldBe(TaskState.Paused));
        await fixture.Run<IServerTaskControlService>(control => control.ResumeTaskAsync(taskId));
        await fixture.Run<IDeploymentTaskExecutor>(fixture.Deliveries.Skip(initialDeliveries + 1).Single());

        fixture.ExecutionCapture.CapturedRequests.Single().ScriptBody.ShouldContain("trigger-smoke");
        await fixture.Run<IServerTaskDataProvider>(async tasks =>
            (await tasks.GetServerTaskByIdNoTrackingAsync(taskId)).State.ShouldBe(TaskState.Success));
    }

    [Fact]
    public async Task RevokingDeploymentPermissionAfterObservation_PreventsReleaseAndDeploymentCreation()
    {
        var input = await SeedAsync();
        var triggerId = await CreateTriggerAsync(input);
        (await ObserveAsync(triggerId)).ShouldBe(1);
        await fixture.Run<SquidDbContext>(db => db.Set<UserRolePermission>()
            .Where(p => p.UserRoleId == input.RoleId && p.Permission == nameof(Permission.DeploymentCreate)).ExecuteDeleteAsync());

        await ExecuteAsync();

        var run = await HistoryAsync(input, triggerId);
        run.Status.ShouldBe(ProjectTriggerRunStatuses.Failed, run.Reason);
        run.Reason.ShouldNotBeNullOrWhiteSpace();
        run.ReleaseId.ShouldBeNull();
        run.DeploymentId.ShouldBeNull();
        run.TaskId.ShouldBeNull();
        await fixture.Run<SquidDbContext>(async db =>
        {
            (await db.Set<Release>().AnyAsync(r => r.ProjectId == input.ProjectId)).ShouldBeFalse();
            (await db.Set<ServerTask>().AnyAsync(t => t.ProjectId == input.ProjectId)).ShouldBeFalse();
        });
    }

    private Task<int> ObserveAsync(int id) => fixture.Run<IProjectTriggerObserver, int>(o => o.ObserveAsync(id,
        new TriggerObservation("fixture", "{}", DateTimeOffset.UtcNow)));

    private Task<int> ExecuteAsync() => fixture.Run<IProjectTriggerWorker, int>(w => w.ExecutePendingAsync(10));

    private async Task<int> CreateTriggerAsync(TriggerInput input)
    {
        var command = new CreateProjectTriggerCommand
        {
            ProjectId = input.ProjectId, Name = "Release and deploy", TriggerType = ProjectTriggerTypes.Scheduled,
            ActionType = ProjectTriggerActions.DeployNewRelease, Configuration = JsonSerializer.SerializeToElement(
                new { schemaVersion = 1, channelId = input.ChannelId, environmentId = input.EnvironmentId })
        };
        var response = await fixture.Run<IMediator, CreateProjectTriggerResponse>(
            m => m.SendAsync<CreateProjectTriggerCommand, CreateProjectTriggerResponse>(command),
            b => b.RegisterInstance<ICurrentUser>(new Actor(input.UserId)));

        response.Data.ExecutionUserId.ShouldBe(input.UserId);
        return response.Data.Id;
    }

    private async Task<ProjectTriggerRunDto> HistoryAsync(TriggerInput input, int id)
    {
        var history = await fixture.Run<IMediator, GetProjectTriggerHistoryResponse>(m =>
            m.RequestAsync<GetProjectTriggerHistoryRequest, GetProjectTriggerHistoryResponse>(
                new GetProjectTriggerHistoryRequest { ProjectId = input.ProjectId, TriggerId = id }),
            b => b.RegisterInstance<ICurrentUser>(new Actor(input.UserId)));

        history.Data.Count.ShouldBe(1);
        return history.Data.Items.Single();
    }

    private async Task<TriggerInput> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        await using var scope = fixture.LifetimeScope.BeginLifetimeScope();
        var db = scope.Resolve<SquidDbContext>();
        var data = new TestDataBuilder(scope.Resolve<IRepository>(), scope.Resolve<IUnitOfWork>());
        if (!await db.Set<Space>().AnyAsync(s => s.Id == 1))
        {
            db.Add(new Space { Id = 1, Name = "Default", Slug = "default", IsDefault = true });
            await db.SaveChangesAsync();
        }

        var environment = await data.CreateEnvironmentAsync($"Target {suffix}");
        var lifecycle = await data.CreateLifecycleAsync($"Lifecycle {suffix}");
        await data.CreateLifecyclePhaseAsync(lifecycle.Id, environment.Id);
        var process = await data.CreateDeploymentProcessAsync();
        var variables = await data.CreateVariableSetAsync();
        var group = await data.CreateProjectGroupAsync($"Group {suffix}");
        var project = await data.CreateProjectAsync(variables.Id, process.Id, group.Id, lifecycle.Id, $"Project {suffix}");
        process.ProjectId = project.Id;
        await db.SaveChangesAsync();
        await data.UpdateVariableSetOwnerAsync(variables, project.Id);
        var step = await data.CreateDeploymentStepAsync(process.Id, 1, "Smoke test");
        await data.CreateStepPropertiesAsync(step.Id, (SpecialVariables.Step.RunOnServer, "true"));
        var action = await data.CreateDeploymentActionAsync(step.Id, 1, "Run smoke test", actionType: "Squid.Script");
        await data.CreateActionPropertiesAsync(action.Id, (SpecialVariables.Action.ScriptBody, "echo trigger-smoke"),
            (SpecialVariables.Action.ScriptSyntax, "Bash"));
        var channel = await db.Set<Channel>().SingleAsync(c => c.ProjectId == project.Id && c.IsDefault);
        var (userId, roleId) = await GrantPermissionsAsync(db, suffix);

        return new(project.Id, channel.Id, environment.Id, userId, roleId);
    }

    private static async Task<(int User, int Role)> GrantPermissionsAsync(SquidDbContext db, string name)
    {
        var user = new UserAccount { UserName = name, NormalizedUserName = name.ToUpperInvariant(), DisplayName = name, PasswordHash = "" };
        var team = new Team { Name = name, SpaceId = 1 };
        var role = new UserRole { Name = name };
        db.AddRange(user, team, role);
        await db.SaveChangesAsync();
        db.AddRange(new TeamMember { TeamId = team.Id, UserId = user.Id },
            new ScopedUserRole { TeamId = team.Id, SpaceId = 1, UserRoleId = role.Id });
        var permissions = new[] { Permission.ProjectView, Permission.ProjectEdit, Permission.ReleaseCreate,
            Permission.ReleaseView, Permission.DeploymentCreate, Permission.DeploymentView, Permission.EnvironmentView };
        db.AddRange(permissions.Select(p => new UserRolePermission { UserRoleId = role.Id, Permission = p.ToString() }));
        await db.SaveChangesAsync();

        return (user.Id, role.Id);
    }

    private sealed record TriggerInput(int ProjectId, int ChannelId, int EnvironmentId, int UserId, int RoleId);

    private sealed class Actor(int id) : ICurrentUser
    {
        public int? Id => id;
        public string Name => $"trigger-author-{id}";
        public bool IsInternal => false;
    }
}
