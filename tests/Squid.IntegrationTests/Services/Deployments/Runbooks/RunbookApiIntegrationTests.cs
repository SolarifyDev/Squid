using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Runbooks;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Teams;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Runbook;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Requests.Deployments.Process;
using Squid.Message.Requests.Deployments.Runbook;
using SquidProject = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.IntegrationTests.Services.Deployments.Runbooks;

[Collection("RunbookApi")]
public class RunbookApiIntegrationTests : TestBase
{
    private const int ViewerUserId = 5101;
    private const int EditorUserId = 5102;
    private const int OtherProjectViewerUserId = 5103;
    private static readonly Guid TestTopic = Guid.NewGuid();

    public RunbookApiIntegrationTests()
        : base(
            $"runbook_api_{TestTopic:N}",
            $"squid_it_runbook_api_{TestTopic:N}")
    {
    }

    [Fact]
    public async Task ApiMessages_CreateListDetailUpdateCopyDelete_WorkTogether()
    {
        var fixture = await SeedProjectAsync();
        await AssignPermissionsAsync(
            EditorUserId,
            fixture.Project,
            Permission.RunbookView,
            Permission.RunbookEdit);

        await Run<IMediator>(
            async mediator =>
            {
                var created = await mediator.SendAsync<CreateRunbookCommand, CreateRunbookResponse>(
                    new CreateRunbookCommand
                    {
                        Runbook = new CreateRunbookModel
                        {
                            ProjectId = fixture.Project.Id,
                            SpaceId = fixture.Project.SpaceId,
                            Name = "  API Recovery  ",
                            Description = "  recovery process  ",
                            Tags = [" ops ", "OPS"]
                        }
                    });

                created.Data.Name.ShouldBe("API Recovery");
                created.Data.ProjectId.ShouldBe(fixture.Project.Id);
                created.Data.ProcessId.ShouldBeGreaterThan(0);

                var listed = await mediator.RequestAsync<GetRunbooksRequest, GetRunbooksResponse>(
                    new GetRunbooksRequest
                    {
                        ProjectId = fixture.Project.Id,
                        SpaceId = fixture.Project.SpaceId,
                        PageIndex = 1,
                        PageSize = 1
                    });
                listed.Data.Count.ShouldBe(1);
                listed.Data.Runbooks.Single().Id.ShouldBe(created.Data.Id);

                var detail = await mediator.RequestAsync<GetRunbookRequest, GetRunbookResponse>(
                    new GetRunbookRequest
                    {
                        Id = created.Data.Id,
                        ProjectId = fixture.Project.Id,
                        SpaceId = fixture.Project.SpaceId
                    });
                detail.Data.ProcessId.ShouldBe(created.Data.ProcessId);

                var updated = await mediator.SendAsync<UpdateRunbookCommand, UpdateRunbookResponse>(
                    new UpdateRunbookCommand
                    {
                        Id = created.Data.Id,
                        ProjectId = fixture.Project.Id,
                        SpaceId = fixture.Project.SpaceId,
                        Runbook = new UpdateRunbookModel
                        {
                            Name = "API Recovery Updated",
                            Tags = ["updated"]
                        }
                    });
                updated.Data.ProcessId.ShouldBe(created.Data.ProcessId);

                var copied = await mediator.SendAsync<CopyRunbookCommand, CopyRunbookResponse>(
                    new CopyRunbookCommand
                    {
                        SourceRunbookId = created.Data.Id,
                        ProjectId = fixture.Project.Id,
                        SpaceId = fixture.Project.SpaceId
                    });
                copied.Data.Id.ShouldNotBe(created.Data.Id);
                copied.Data.ProcessId.ShouldNotBe(created.Data.ProcessId);

                var deleted = await mediator.SendAsync<DeleteRunbookCommand, DeleteRunbookResponse>(
                    new DeleteRunbookCommand
                    {
                        Id = copied.Data.Id,
                        ProjectId = fixture.Project.Id,
                        SpaceId = fixture.Project.SpaceId
                    });
                deleted.Data.Id.ShouldBe(copied.Data.Id);
            },
            CurrentUser(EditorUserId));

        await Run<SquidDbContext>(async db =>
        {
            db.ChangeTracker.Clear();
            var runbooks = await db.Set<Runbook>()
                .Where(runbook => runbook.ProjectId == fixture.Project.Id)
                .ToListAsync();
            runbooks.Count.ShouldBe(1);
            runbooks[0].Name.ShouldBe("API Recovery Updated");
            (await db.Set<DeploymentProcess>().AnyAsync(
                process => process.Id == fixture.Project.DeploymentProcessId)).ShouldBeTrue();
        });
    }

    [Fact]
    public async Task ApiMessages_DuplicateInvalidAndMissingInputs_AreRejectedWithoutMutation()
    {
        var fixture = await SeedProjectAsync();
        await AssignPermissionsAsync(
            EditorUserId,
            fixture.Project,
            Permission.RunbookView,
            Permission.RunbookEdit);

        await Run<IMediator>(
            async mediator =>
            {
                await mediator.SendAsync<CreateRunbookCommand, CreateRunbookResponse>(
                    new CreateRunbookCommand
                    {
                        Runbook = new CreateRunbookModel
                        {
                            ProjectId = fixture.Project.Id,
                            SpaceId = fixture.Project.SpaceId,
                            Name = "Recovery"
                        }
                    });

                var conflict = await Should.ThrowAsync<InvalidOperationException>(() =>
                    mediator.SendAsync<CreateRunbookCommand, CreateRunbookResponse>(
                        new CreateRunbookCommand
                        {
                            Runbook = new CreateRunbookModel
                            {
                                ProjectId = fixture.Project.Id,
                                SpaceId = fixture.Project.SpaceId,
                                Name = " recovery "
                            }
                        }));
                conflict.Message.ShouldContain("already exists");

                var validation = await Should.ThrowAsync<FluentValidation.ValidationException>(() =>
                    mediator.SendAsync<CreateRunbookCommand, CreateRunbookResponse>(
                        new CreateRunbookCommand
                        {
                            Runbook = new CreateRunbookModel
                            {
                                ProjectId = fixture.Project.Id,
                                SpaceId = fixture.Project.SpaceId,
                                Name = " "
                            }
                        }));
                validation.Errors.ShouldContain(error => error.PropertyName == "Runbook.Name");

                await Should.ThrowAsync<InvalidOperationException>(() =>
                    mediator.RequestAsync<GetRunbookRequest, GetRunbookResponse>(
                        new GetRunbookRequest
                        {
                            Id = 999999,
                            ProjectId = fixture.Project.Id,
                            SpaceId = fixture.Project.SpaceId
                        }));
            },
            CurrentUser(EditorUserId));

        await Run<SquidDbContext>(async db =>
        {
            db.ChangeTracker.Clear();
            (await db.Set<Runbook>().CountAsync(
                runbook => runbook.ProjectId == fixture.Project.Id)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task ApiMessages_ListIsProjectScopedAndUsesPersistedAuthorizationScope()
    {
        var fixture = await SeedProjectAsync();
        await AssignPermissionsAsync(
            OtherProjectViewerUserId,
            fixture.OtherProject,
            Permission.RunbookView);

        await Run<IRunbookService>(
            async service =>
            {
                await service.CreateRunbookAsync(new CreateRunbookCommand
                {
                    Runbook = new CreateRunbookModel
                    {
                        ProjectId = fixture.Project.Id,
                        SpaceId = fixture.Project.SpaceId,
                        Name = "Project A Runbook"
                    }
                });
            });
        await Run<IRunbookService>(
            async service =>
            {
                await service.CreateRunbookAsync(new CreateRunbookCommand
                {
                    Runbook = new CreateRunbookModel
                    {
                        ProjectId = fixture.OtherProject.Id,
                        SpaceId = fixture.OtherProject.SpaceId,
                        Name = "Project B Runbook"
                    }
                });
            });

        await Run<IMediator>(
            async mediator =>
            {
                var denied = await Should.ThrowAsync<InvalidOperationException>(() =>
                    mediator.RequestAsync<GetRunbooksRequest, GetRunbooksResponse>(
                        new GetRunbooksRequest
                        {
                            ProjectId = fixture.Project.Id,
                            SpaceId = fixture.Project.SpaceId,
                            PageIndex = 1,
                            PageSize = 20
                        }));
                denied.Message.ShouldContain(nameof(Permission.RunbookView));

                var allowed = await mediator.RequestAsync<GetRunbooksRequest, GetRunbooksResponse>(
                    new GetRunbooksRequest
                    {
                        ProjectId = fixture.OtherProject.Id,
                        SpaceId = fixture.OtherProject.SpaceId,
                        PageIndex = 1,
                        PageSize = 20
                    });
                allowed.Data.Count.ShouldBe(1);
                allowed.Data.Runbooks.Single().ProjectId.ShouldBe(fixture.OtherProject.Id);
            },
            CurrentUser(OtherProjectViewerUserId));
    }

    [Fact]
    public async Task ApiMessages_RunbookProcessDetail_RequiresRunbookReadAndRejectsMismatch()
    {
        var fixture = await SeedProjectAsync();
        int runbookId = 0;
        int processId = 0;
        await Run<IRunbookService>(
            async service =>
            {
                var created = await service.CreateRunbookAsync(new CreateRunbookCommand
                {
                    Runbook = new CreateRunbookModel
                    {
                        ProjectId = fixture.Project.Id,
                        SpaceId = fixture.Project.SpaceId,
                        Name = "Process Context"
                    }
                });
                runbookId = created.Id;
                processId = created.ProcessId;
            });

        await AssignPermissionsAsync(
            ViewerUserId,
            fixture.Project,
            Permission.RunbookView);

        await Run<IMediator>(
            async mediator =>
            {
                var detail = await mediator.RequestAsync<GetDeploymentProcessRequest, GetDeploymentProcessResponse>(
                    new GetDeploymentProcessRequest
                    {
                        Id = processId,
                        ProjectId = fixture.Project.Id,
                        SpaceId = fixture.Project.SpaceId,
                        RunbookId = runbookId
                    });
                detail.Data.DeploymentProcess.Id.ShouldBe(processId);

                await Should.ThrowAsync<ProcessContextMismatchException>(() =>
                    mediator.RequestAsync<GetDeploymentProcessRequest, GetDeploymentProcessResponse>(
                        new GetDeploymentProcessRequest
                        {
                            Id = processId,
                            ProjectId = fixture.Project.Id,
                            SpaceId = fixture.Project.SpaceId
                        }));
            },
            CurrentUser(ViewerUserId));

        await Run<IMediator>(
            async mediator =>
            {
                await Should.ThrowAsync<ProcessContextMismatchException>(() =>
                    mediator.RequestAsync<GetDeploymentProcessRequest, GetDeploymentProcessResponse>(
                        new GetDeploymentProcessRequest
                        {
                            Id = processId,
                            ProjectId = fixture.Project.Id,
                            SpaceId = fixture.Project.SpaceId
                        }));
            },
            InternalCurrentUser());

        await Run<IMediator>(
            async mediator =>
            {
                var denied = await Should.ThrowAsync<InvalidOperationException>(() =>
                    mediator.SendAsync<CreateDeploymentStepCommand, CreateDeploymentStepResponse>(
                        new CreateDeploymentStepCommand
                        {
                            ProcessId = processId,
                            ProjectId = fixture.Project.Id,
                            SpaceId = fixture.Project.SpaceId,
                            RunbookId = runbookId,
                            Step = new CreateOrUpdateDeploymentStepModel
                            {
                                Name = "Denied step",
                                StepType = "Action",
                                Condition = "Success",
                                Properties = [],
                                Actions = []
                            }
                        }));
                denied.Message.ShouldContain(nameof(Permission.RunbookEdit));
            },
            CurrentUser(ViewerUserId));
    }

    private async Task<Fixture> SeedProjectAsync()
    {
        Fixture fixture = null;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            var projectProcess = await builder.CreateProjectDeploymentProcessAsync("Runbook API Project");
            var project = await repository.GetByIdAsync<SquidProject>(projectProcess.ProjectId);

            var otherProcess = await builder.CreateProjectDeploymentProcessAsync("Other Runbook API Project");
            var otherProject = await repository.GetByIdAsync<SquidProject>(otherProcess.ProjectId);

            fixture = new Fixture
            {
                Project = project,
                OtherProject = otherProject
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
                    Name = $"Runbook API Team {userId}",
                    Description = "Runbook API integration test team",
                    SpaceId = scopeProject.SpaceId
                };
                await teamProvider.AddAsync(team);
                await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = userId });

                var role = new UserRole
                {
                    Name = $"Runbook API Role {userId}",
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

    private static Action<ContainerBuilder> CurrentUser(int userId) =>
        builder => builder.RegisterInstance<ICurrentUser>(new FixedCurrentUser(userId)).SingleInstance();

    private static Action<ContainerBuilder> InternalCurrentUser() =>
        builder => builder.RegisterInstance<ICurrentUser>(new FixedInternalUser()).SingleInstance();

    private sealed class Fixture
    {
        public SquidProject Project { get; init; }
        public SquidProject OtherProject { get; init; }
    }

    private sealed class FixedCurrentUser(int id) : ICurrentUser
    {
        public int? Id => id;
        public string Name => $"runbook-api-user-{id}";
        public bool IsInternal => false;
    }

    private sealed class FixedInternalUser : ICurrentUser
    {
        public int? Id => 8888;
        public string Name => "System";
        public bool IsInternal => true;
    }
}

[CollectionDefinition("RunbookApi")]
public sealed class RunbookApiCollection
{
}
