using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Teams;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Runbook;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Requests.Deployments.Process.Step;
using Squid.Message.Requests.Deployments.Runbook;
using SquidProject = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.IntegrationTests.Services.Authorization;

/// <summary>
/// Exercises the real mediator authorization pipeline for Runbook
/// commands and the shared process/step commands. The tests intentionally
/// avoid controller endpoints: chapter 4 covers message permissions,
/// persisted-context authorization, and audit; chapter 5 owns API routing.
/// </summary>
[Collection("RunbookAuthorization")]
public class RunbookAuthorizationIntegrationTests : TestBase
{
    private const int ViewerUserId = 4101;
    private const int EditorUserId = 4102;
    private const int ProcessEditorUserId = 4103;
    private const int ScopedViewerUserId = 4104;
    private static readonly Guid TestTopic = Guid.NewGuid();

    public RunbookAuthorizationIntegrationTests()
        : base(
            $"runbook_authorization_{TestTopic:N}",
            $"squid_it_runbook_authorization_{TestTopic:N}")
    {
    }

    [Fact]
    public async Task RunbookView_CanReadButCannotMutate()
    {
        var fixture = await SeedFixtureAsync();
        await AssignPermissionsAsync(
            ViewerUserId,
            fixture.Project,
            Permission.RunbookView);

        await Run<IMediator>(
            async mediator =>
            {
                var detail = await mediator.RequestAsync<GetRunbookRequest, GetRunbookResponse>(
                    new GetRunbookRequest
                    {
                        Id = fixture.Runbook.Id,
                        ProjectId = fixture.Project.Id,
                        SpaceId = fixture.Project.SpaceId
                    });

                detail.Data.Id.ShouldBe(fixture.Runbook.Id);

                var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
                    mediator.SendAsync<UpdateRunbookCommand, UpdateRunbookResponse>(
                        new UpdateRunbookCommand
                        {
                            Id = fixture.Runbook.Id,
                            ProjectId = fixture.Project.Id,
                            SpaceId = fixture.Project.SpaceId,
                            Runbook = new UpdateRunbookModel { Name = "Viewer mutation" }
                        }));

                exception.Message.ShouldContain(nameof(Permission.RunbookEdit));
            },
            CurrentUser(ViewerUserId));

        await AssertRunbookNameAsync(fixture.Runbook.Id, "Persisted Runbook");
    }

    [Fact]
    public async Task RunbookEdit_CanMutateRunbookButCannotMutateProjectDeploymentProcess()
    {
        var fixture = await SeedFixtureAsync();
        await AssignPermissionsAsync(
            EditorUserId,
            fixture.Project,
            Permission.RunbookView,
            Permission.RunbookEdit);

        await Run<IMediator>(
            async mediator =>
            {
                await mediator.SendAsync<UpdateRunbookCommand, UpdateRunbookResponse>(
                    new UpdateRunbookCommand
                    {
                        Id = fixture.Runbook.Id,
                        ProjectId = fixture.Project.Id,
                        SpaceId = fixture.Project.SpaceId,
                        Runbook = new UpdateRunbookModel { Name = "Editor mutation" }
                    });

                await mediator.SendAsync<UpdateDeploymentStepCommand, UpdateDeploymentStepResponse>(
                    new UpdateDeploymentStepCommand
                    {
                        Id = fixture.RunbookStep.Id,
                        ProjectId = fixture.Project.Id,
                        RunbookId = fixture.Runbook.Id,
                        SpaceId = fixture.Project.SpaceId,
                        Step = ReplacementStep("Runbook editor mutation")
                    });

                var denied = await Should.ThrowAsync<InvalidOperationException>(() =>
                    mediator.SendAsync<UpdateDeploymentStepCommand, UpdateDeploymentStepResponse>(
                        new UpdateDeploymentStepCommand
                        {
                            Id = fixture.ProjectStep.Id,
                            ProjectId = fixture.Project.Id,
                            SpaceId = fixture.Project.SpaceId,
                            Step = ReplacementStep("Project process mutation")
                        }));

                denied.Message.ShouldContain(nameof(Permission.ProcessEdit));
            },
            CurrentUser(EditorUserId));

        await AssertRunbookNameAsync(fixture.Runbook.Id, "Editor mutation");
        await AssertStepNameAsync(fixture.RunbookStep.Id, "Runbook editor mutation");
        await AssertStepNameAsync(fixture.ProjectStep.Id, "Project step");
    }

    [Fact]
    public async Task ProcessEdit_CannotMutateRunbookProcess()
    {
        var fixture = await SeedFixtureAsync();
        await AssignPermissionsAsync(
            ProcessEditorUserId,
            fixture.Project,
            Permission.ProcessView,
            Permission.ProcessEdit);

        await Run<IMediator>(
            async mediator =>
            {
                var denied = await Should.ThrowAsync<InvalidOperationException>(() =>
                    mediator.SendAsync<UpdateDeploymentStepCommand, UpdateDeploymentStepResponse>(
                        new UpdateDeploymentStepCommand
                        {
                            Id = fixture.RunbookStep.Id,
                            ProjectId = fixture.Project.Id,
                            RunbookId = fixture.Runbook.Id,
                            SpaceId = fixture.Project.SpaceId,
                            Step = ReplacementStep("Process permission mutation")
                        }));

                denied.Message.ShouldContain(nameof(Permission.RunbookEdit));
            },
            CurrentUser(ProcessEditorUserId));

        await AssertStepNameAsync(fixture.RunbookStep.Id, "Runbook step");
    }

    [Fact]
    public async Task RunbookView_ScopedToDifferentProject_IsDeniedAndUsesPersistedOwnership()
    {
        var fixture = await SeedFixtureAsync();
        await AssignPermissionsAsync(
            ScopedViewerUserId,
            fixture.OtherProject,
            Permission.RunbookView);

        await Run<IMediator>(
            async mediator =>
            {
                var denied = await Should.ThrowAsync<InvalidOperationException>(() =>
                    mediator.RequestAsync<GetRunbookRequest, GetRunbookResponse>(
                        new GetRunbookRequest
                        {
                            Id = fixture.Runbook.Id,
                            ProjectId = fixture.Project.Id,
                            SpaceId = fixture.Project.SpaceId
                        }));

                denied.Message.ShouldContain(nameof(Permission.RunbookView));
            },
            CurrentUser(ScopedViewerUserId));
    }

    [Fact]
    public async Task RunbookView_ForDifferentSpace_IsDenied()
    {
        var fixture = await SeedFixtureAsync();
        await AssignPermissionsAsync(
            ViewerUserId,
            fixture.Project,
            Permission.RunbookView);

        await Run<IMediator>(
            async mediator =>
            {
                var denied = await Should.ThrowAsync<InvalidOperationException>(() =>
                    mediator.RequestAsync<GetRunbookRequest, GetRunbookResponse>(
                        new GetRunbookRequest
                        {
                            Id = fixture.Runbook.Id,
                            ProjectId = fixture.Project.Id,
                            SpaceId = fixture.Project.SpaceId + 1
                        }));

                denied.Message.ShouldContain(nameof(Permission.RunbookView));
            },
            CurrentUser(ViewerUserId));
    }

    private async Task<Fixture> SeedFixtureAsync()
    {
        Fixture fixture = null;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            var projectProcess = await builder.CreateProjectDeploymentProcessAsync("Runbook Auth Project");
            var project = await repository.GetByIdAsync<SquidProject>(projectProcess.ProjectId);

            var otherProcess = await builder.CreateProjectDeploymentProcessAsync("Other Runbook Auth Project");
            var otherProject = await repository.GetByIdAsync<SquidProject>(otherProcess.ProjectId);

            var runbook = await builder.CreateRunbookAsync(project.Id, project.SpaceId, "Persisted Runbook");
            var runbookStep = await builder.CreateDeploymentStepAsync(runbook.ProcessId, 1, "Runbook step");
            var projectStep = await builder.CreateDeploymentStepAsync(project.DeploymentProcessId, 1, "Project step");

            fixture = new Fixture
            {
                Project = project,
                OtherProject = otherProject,
                Runbook = runbook,
                RunbookStep = runbookStep,
                ProjectStep = projectStep
            };
        });

        return fixture;
    }

    private async Task AssignPermissionsAsync(
        int userId,
        SquidProject scopeProject,
        params Permission[] permissions)
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(
            async (teamProvider, roleProvider, scopedRoleProvider) =>
            {
                var team = new Team
                {
                    Name = $"Authorization Team {userId}",
                    Description = "Runbook authorization test team",
                    SpaceId = scopeProject.SpaceId
                };
                await teamProvider.AddAsync(team);
                await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = userId });

                var role = new UserRole
                {
                    Name = $"Authorization Role {userId}",
                    IsBuiltIn = false
                };
                await roleProvider.AddAsync(role);
                await roleProvider.SetPermissionsAsync(
                    role.Id,
                    permissions.Select(permission => permission.ToString()).ToList());

                var scopedRole = new ScopedUserRole
                {
                    TeamId = team.Id,
                    UserRoleId = role.Id,
                    SpaceId = scopeProject.SpaceId
                };
                await scopedRoleProvider.AddAsync(scopedRole);
                await scopedRoleProvider.SetProjectScopeAsync(scopedRole.Id, [scopeProject.Id]);
            });
    }

    private async Task AssertRunbookNameAsync(int runbookId, string expected)
    {
        await Run<SquidDbContext>(async db =>
        {
            db.ChangeTracker.Clear();
            (await db.Set<Runbook>().SingleAsync(runbook => runbook.Id == runbookId))
                .Name.ShouldBe(expected);
        });
    }

    private async Task AssertStepNameAsync(int stepId, string expected)
    {
        await Run<SquidDbContext>(async db =>
        {
            db.ChangeTracker.Clear();
            (await db.Set<DeploymentStep>().SingleAsync(step => step.Id == stepId))
                .Name.ShouldBe(expected);
        });
    }

    private static Action<ContainerBuilder> CurrentUser(int userId) =>
        builder => builder.RegisterInstance<ICurrentUser>(new FixedCurrentUser(userId)).SingleInstance();

    private static CreateOrUpdateDeploymentStepModel ReplacementStep(string name) => new()
    {
        Name = name,
        StepType = "Action",
        Condition = "Success",
        Properties = [],
        Actions = []
    };

    private sealed class Fixture
    {
        public SquidProject Project { get; init; }
        public SquidProject OtherProject { get; init; }
        public Runbook Runbook { get; init; }
        public DeploymentStep RunbookStep { get; init; }
        public DeploymentStep ProjectStep { get; init; }
    }

    private sealed class FixedCurrentUser(int id) : ICurrentUser
    {
        public int? Id => id;
        public string Name => $"authorization-user-{id}";
        public bool IsInternal => false;
    }
}

[CollectionDefinition("RunbookAuthorization")]
public sealed class RunbookAuthorizationCollection
{
}
