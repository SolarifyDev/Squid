using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Deployments.Interruption;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Requests.Deployments.Interruption;

namespace Squid.IntegrationTests.Services.Deployments;

/// <summary>
/// Administrator approval override on REAL Postgres with REAL roles, teams and AuthorizationService, driven
/// through the REAL mediator pipeline (space scope + authorization middleware + handlers): a manual
/// intervention whose responsible team has no members (the shape an Octopus import produces) can be
/// approved by a Space Owner of the interruption's space or a System Administrator — and by nobody else
/// who is not a team member. Team-member behaviour is unchanged.
/// </summary>
public class InterruptionAdministratorOverrideIntegrationTests : TestBase
{
    private const int SpaceId = 1;
    private const int OtherSpaceId = 2;

    private const int SpaceOwnerUserId = 501;
    private const int OtherSpaceOwnerUserId = 502;
    private const int DeployerUserId = 503;
    private const int TeamMemberUserId = 504;
    private const int SystemAdminUserId = 505;

    public InterruptionAdministratorOverrideIntegrationTests()
        : base("InterruptionAdministratorOverride", "squid_it_interruption_admin_override")
    {
    }

    [Theory]
    [InlineData(SpaceOwnerUserId, true)]
    [InlineData(SystemAdminUserId, true)]
    [InlineData(TeamMemberUserId, false)]
    public async Task Submit_AllowedCaller_ResolvesTheInterruption_AndRecordsOnlyGenuineOverrides(int userId, bool expectOverride)
    {
        var (interruptionId, taskId) = await SeedAsync(teamMembers: [TeamMemberUserId]);

        await SubmitAsAsync(userId, interruptionId);

        await Run<SquidDbContext>(async db =>
        {
            var interruption = await db.Set<DeploymentInterruption>().AsNoTracking().SingleAsync(i => i.Id == interruptionId);
            interruption.Resolution.ShouldBe(nameof(InterruptionOutcome.Proceed));
            interruption.AdministratorOverrideUserId.ShouldBe(expectOverride ? userId : null);
            interruption.AdministratorOverrideUserName.ShouldBe(expectOverride ? $"user-{userId}" : null);

            (await db.Set<ServerTask>().AsNoTracking().SingleAsync(t => t.Id == taskId)).HasPendingInterruptions.ShouldBeFalse();
        });
    }

    [Fact]
    public async Task Submit_NonMemberWithInterruptionSubmitButNoOverride_IsRejectedAndNothingChanges()
    {
        var (interruptionId, _) = await SeedAsync(teamMembers: []);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => SubmitAsAsync(DeployerUserId, interruptionId));
        ex.Message.ShouldContain(nameof(Permission.InterruptionSubmitOverride));

        await AssertStillPendingAsync(interruptionId);
    }

    [Fact]
    public async Task Submit_SpaceOwnerOfAnotherSpace_IsRejectedAndNothingChanges()
    {
        var (interruptionId, _) = await SeedAsync(teamMembers: []);

        await Should.ThrowAsync<PermissionDeniedException>(() => SubmitAsAsync(OtherSpaceOwnerUserId, interruptionId));

        await AssertStillPendingAsync(interruptionId);
    }

    [Fact]
    public async Task Submit_SpaceOwnerOfAnotherSpace_EvenWhenAddressingTheirOwnSpace_CannotOverride()
    {
        // The command's space header is the caller's own space (passes the middleware), but the override is
        // checked against the INTERRUPTION's space, so a header mismatch cannot smuggle approval rights.
        var (interruptionId, _) = await SeedAsync(teamMembers: []);

        await Should.ThrowAsync<InvalidOperationException>(() => SubmitAsAsync(OtherSpaceOwnerUserId, interruptionId, commandSpaceId: OtherSpaceId));

        await AssertStillPendingAsync(interruptionId);
    }

    [Theory]
    [InlineData(SpaceOwnerUserId, true, true)]
    [InlineData(SystemAdminUserId, true, true)]
    [InlineData(TeamMemberUserId, true, false)]
    [InlineData(DeployerUserId, false, false)]
    public async Task GetPending_ReflectsTheOverride(int userId, bool expectCanAct, bool expectOverride)
    {
        var (_, taskId) = await SeedAsync(teamMembers: [TeamMemberUserId]);

        GetPendingInterruptionsResponse response = null;

        await Run<IMediator>(
            async mediator => response = await mediator.RequestAsync<GetPendingInterruptionsRequest, GetPendingInterruptionsResponse>(new GetPendingInterruptionsRequest { SpaceId = SpaceId, ServerTaskId = taskId }),
            builder => builder.RegisterInstance<ICurrentUser>(new FixedCurrentUser(userId)).SingleInstance());

        var dto = response.Interruptions.Single();
        dto.CanTakeResponsibility.ShouldBe(expectCanAct);
        dto.IsAdministratorOverride.ShouldBe(expectOverride);
    }

    [Theory]
    [InlineData(SpaceOwnerUserId)]
    [InlineData(TeamMemberUserId)]
    public async Task TakeResponsibility_ByMemberOrOverride_AssignsTheUser(int userId)
    {
        var (interruptionId, _) = await SeedAsync(teamMembers: [TeamMemberUserId]);

        await Run<IMediator>(
            async mediator => await mediator.SendAsync<TakeResponsibilityCommand, TakeResponsibilityResponse>(new TakeResponsibilityCommand { SpaceId = SpaceId, InterruptionId = interruptionId, UserId = userId.ToString() }),
            builder => builder.RegisterInstance<ICurrentUser>(new FixedCurrentUser(userId)).SingleInstance());

        await Run<SquidDbContext>(async db =>
        {
            var interruption = await db.Set<DeploymentInterruption>().AsNoTracking().SingleAsync(i => i.Id == interruptionId);
            interruption.ResponsibleUserId.ShouldBe(userId.ToString());
            interruption.Resolution.ShouldBeNull();
            interruption.AdministratorOverrideUserId.ShouldBeNull(customMessage: "taking responsibility is not a resolution");
        });
    }

    [Fact]
    public async Task Submit_InterruptionWithoutResponsibleTeams_StaysOpenToAnySubmitterWithoutOverride()
    {
        var (interruptionId, _) = await SeedAsync(teamMembers: [], responsibleTeams: false);

        await SubmitAsAsync(DeployerUserId, interruptionId);

        await Run<SquidDbContext>(async db =>
        {
            var interruption = await db.Set<DeploymentInterruption>().AsNoTracking().SingleAsync(i => i.Id == interruptionId);
            interruption.Resolution.ShouldBe(nameof(InterruptionOutcome.Proceed));
            interruption.AdministratorOverrideUserId.ShouldBeNull();
        });
    }

    // ── Drivers ─────────────────────────────────────────────────────────────────────────────────

    private async Task SubmitAsAsync(int userId, int interruptionId, int commandSpaceId = SpaceId)
    {
        await Run<IMediator>(
            async mediator => await mediator.SendAsync<SubmitInterruptionCommand, SubmitInterruptionResponse>(new SubmitInterruptionCommand { SpaceId = commandSpaceId, InterruptionId = interruptionId, Values = new Dictionary<string, string> { ["Result"] = "Proceed" } }),
            builder => builder.RegisterInstance<ICurrentUser>(new FixedCurrentUser(userId)).SingleInstance());
    }

    private async Task AssertStillPendingAsync(int interruptionId)
    {
        await Run<SquidDbContext>(async db =>
        {
            var interruption = await db.Set<DeploymentInterruption>().AsNoTracking().SingleAsync(i => i.Id == interruptionId);
            interruption.Resolution.ShouldBeNull();
            interruption.SubmittedValuesJson.ShouldBeNull();
            interruption.AdministratorOverrideUserId.ShouldBeNull();
        });
    }

    private async Task<(int InterruptionId, int TaskId)> SeedAsync(int[] teamMembers, bool responsibleTeams = true)
    {
        var result = (0, 0);

        await Run<ILifetimeScope>(async scope =>
        {
            var teams = scope.Resolve<ITeamDataProvider>();
            var roles = scope.Resolve<IUserRoleDataProvider>();
            var scopedRoles = scope.Resolve<IScopedUserRoleDataProvider>();
            var db = scope.Resolve<SquidDbContext>();

            await GrantRoleAsync(teams, roles, scopedRoles, SpaceOwnerUserId, BuiltInRoles.SpaceOwner, SpaceId);
            await GrantRoleAsync(teams, roles, scopedRoles, OtherSpaceOwnerUserId, BuiltInRoles.SpaceOwner, OtherSpaceId);
            await GrantRoleAsync(teams, roles, scopedRoles, DeployerUserId, BuiltInRoles.ProjectDeployer, SpaceId);
            await GrantRoleAsync(teams, roles, scopedRoles, TeamMemberUserId, BuiltInRoles.ProjectDeployer, SpaceId);
            await GrantRoleAsync(teams, roles, scopedRoles, SystemAdminUserId, BuiltInRoles.SystemAdministrator, null);

            var approvers = new Team { Name = "Imported approvers", SpaceId = SpaceId };
            await teams.AddAsync(approvers);

            foreach (var member in teamMembers)
                await teams.AddMemberAsync(new TeamMember { TeamId = approvers.Id, UserId = member });

            var task = new ServerTask { Name = "Deploy", Description = "override", QueueTime = DateTimeOffset.UtcNow, State = TaskState.Paused, ServerTaskType = "Deploy", SpaceId = SpaceId, LastModifiedDate = DateTimeOffset.UtcNow, BusinessProcessState = "Queued", StateOrder = 1, Weight = 1, JSON = string.Empty, ServerNodeId = Guid.NewGuid(), DataVersion = [], HasPendingInterruptions = true };
            db.Set<ServerTask>().Add(task);
            await db.SaveChangesAsync();

            var interruption = new DeploymentInterruption { ServerTaskId = task.Id, DeploymentId = 0, InterruptionType = InterruptionType.ManualIntervention, StepName = "Approval", ActionName = "Approval", SpaceId = SpaceId, ResponsibleTeamIds = responsibleTeams ? approvers.Id.ToString() : null };
            db.Set<DeploymentInterruption>().Add(interruption);
            await db.SaveChangesAsync();

            result = (interruption.Id, task.Id);
        });

        return result;
    }

    private static async Task GrantRoleAsync(ITeamDataProvider teams, IUserRoleDataProvider roles, IScopedUserRoleDataProvider scopedRoles, int userId, BuiltInRoleDefinition definition, int? spaceId)
    {
        var team = new Team { Name = $"team-of-{userId}", SpaceId = spaceId ?? 0 };
        await teams.AddAsync(team);
        await teams.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = userId });

        var role = await roles.GetByNameAsync(definition.Name);

        if (role == null)
        {
            role = new UserRole { Name = definition.Name, IsBuiltIn = true };
            await roles.AddAsync(role);
            await roles.SetPermissionsAsync(role.Id, definition.Permissions.Select(p => p.ToString()).ToList());
        }

        await scopedRoles.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = spaceId });
    }

    private sealed class FixedCurrentUser(int id) : ICurrentUser
    {
        public int? Id => id;
        public string Name => $"user-{id}";
        public bool IsInternal => false;
    }
}
