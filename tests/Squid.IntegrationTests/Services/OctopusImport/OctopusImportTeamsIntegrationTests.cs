using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using SquidEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier for Octopus TEAM import against a REAL isolated Postgres database: the confirm
/// command driven through the REAL mediator pipeline (space scope + authorization middleware + real
/// AuthorizationService over real roles/teams), built-in team mapping, global-vs-space precedence,
/// the "reused team grants approval to its members" warning + stale detection, and the fail-closed
/// "unmapped responsible team writes nothing" guarantee.
/// </summary>
public class OctopusImportTeamsIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private const int OtherSpaceId = 8;
    private const int ImporterUserId = 4242;

    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public OctopusImportTeamsIntegrationTests()
        : base("OctopusImportTeams", "squid_it_octopus_import_teams")
    {
    }

    // ── C1: confirm through the real mediator as a space-scoped (non-system-admin) user ─────────

    [Fact]
    public async Task Confirm_ThroughRealMediator_AsSpaceScopedUserWithTeamCreate_CreatesTheTeam()
    {
        var archivePath = WriteArchive(Documents(teams: [("Teams-1", "Release Approvers")], manualTeamIds: "Teams-1"));

        try
        {
            await SeedSpaceScopedImporterAsync(SpaceOwnerPermissions());
            var sessionId = await SeedValidatedSessionAsync(archivePath);

            var response = await ConfirmAsImporterAsync(sessionId);

            response.Data.Session.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(response.Data.Session));

            await Run<SquidDbContext>(async db =>
            {
                var team = await db.Set<Team>().AsNoTracking().SingleAsync(t => t.Name == "Release Approvers");
                team.SpaceId.ShouldBe(SpaceId, customMessage: "an imported team belongs to the destination space, never to the system (SpaceId 0) scope");
                team.IsBuiltIn.ShouldBeFalse();

                (await ManualResponsibleTeamIdsAsync(db)).ShouldBe(team.Id.ToString());
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Confirm_ThroughRealMediator_AsSpaceScopedUserWithoutTeamCreate_IsRejectedByPreflightBeforeAnyWrite()
    {
        var archivePath = WriteArchive(Documents(teams: [("Teams-1", "Release Approvers")], manualTeamIds: "Teams-1"));

        try
        {
            await SeedSpaceScopedImporterAsync(SpaceOwnerPermissions().Where(p => p != Permission.TeamCreate).ToList());
            var sessionId = await SeedValidatedSessionAsync(archivePath);

            var ex = await Should.ThrowAsync<PermissionDeniedException>(() => ConfirmAsImporterAsync(sessionId));
            ex.Message.ShouldContain(nameof(Permission.TeamCreate));

            await AssertNothingImportedAsync();
            await AssertSessionStateAsync(sessionId, OctopusImportSessionState.Validated);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Confirm_ThroughRealMediator_AsSpaceOwnerOfAnotherSpace_IsRejectedBeforeAnyWrite()
    {
        var archivePath = WriteArchive(Documents(teams: [("Teams-1", "Release Approvers")], manualTeamIds: "Teams-1"));

        try
        {
            await SeedSpaceScopedImporterAsync(SpaceOwnerPermissions(), roleSpaceId: OtherSpaceId);
            var sessionId = await SeedValidatedSessionAsync(archivePath);

            await Should.ThrowAsync<PermissionDeniedException>(() => ConfirmAsImporterAsync(sessionId));

            await AssertNothingImportedAsync();
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // ── C2: built-in Octopus teams map to Squid built-ins, never duplicated ────────────────────

    [Theory]
    [InlineData("teams-everyone", "Everyone", BuiltInTeamNames.Everyone)]
    [InlineData("teams-administrators", "Octopus Administrators", BuiltInTeamNames.Administrators)]
    public async Task Import_BuiltInOctopusTeamWithSquidEquivalent_ReusesTheSquidBuiltInTeam(string octopusTeamId, string octopusTeamName, string squidTeamName)
    {
        var archivePath = WriteArchive(Documents(teams: [(octopusTeamId, octopusTeamName)], manualTeamIds: octopusTeamId, uniqueIds: false));

        try
        {
            var builtInIds = await SeedSquidBuiltInTeamsAsync();

            var result = await ImportAsInternalUserAsync(archivePath);

            result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(result));

            await Run<SquidDbContext>(async db =>
            {
                (await db.Set<Team>().CountAsync()).ShouldBe(2, customMessage: "a built-in Octopus team must never create a duplicate Squid team");
                (await db.Set<Team>().AnyAsync(t => t.Name == octopusTeamName && !t.IsBuiltIn)).ShouldBeFalse();
                (await ManualResponsibleTeamIdsAsync(db)).ShouldBe(builtInIds[squidTeamName].ToString());
            });

            result.Result.Resources.Single(r => r.SourceId == octopusTeamId).OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Reused);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Preview_BuiltInOctopusTeamWithSquidEquivalent_ButSquidBuiltInMissing_BlocksInsteadOfCreatingADuplicate()
    {
        var archivePath = WriteArchive(Documents(teams: [("teams-administrators", "Octopus Administrators")], manualTeamIds: "teams-administrators", uniqueIds: false));

        try
        {
            await Run<IOctopusImportPlanningPipeline>(async pipeline =>
            {
                var snapshot = await pipeline.BuildPreviewAsync(archivePath, SpaceId);
                var team = snapshot.PreviewPlan.Resources.Single(r => r.SourceId == "teams-administrators");

                team.PreviewAction.ShouldBe(OctopusImportPreviewAction.Blocked);
                team.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.BuiltInTeamEquivalentMissing && d.Severity == OctopusImportCompatibilitySeverity.Blocker);
                snapshot.PreviewPlan.HasBlockers.ShouldBeTrue();
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Import_BuiltInOctopusTeamWithoutSquidEquivalent_CreatesARegularEmptyTeamWithAClearWarning()
    {
        var archivePath = WriteArchive(Documents(teams: [("teams-spacemanagers-Spaces-1", "Space Managers")], manualTeamIds: "teams-spacemanagers-Spaces-1", uniqueIds: false));

        try
        {
            await SeedSquidBuiltInTeamsAsync();

            var result = await ImportAsInternalUserAsync(archivePath);

            result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(result));
            result.Result.Resources.SelectMany(r => r.Diagnostics).ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.BuiltInTeamWithoutEquivalent && d.Severity == OctopusImportCompatibilitySeverity.Warning);

            await Run<SquidDbContext>(async db =>
            {
                var team = await db.Set<Team>().AsNoTracking().SingleAsync(t => t.Name == "Space Managers");
                team.SpaceId.ShouldBe(SpaceId);
                team.IsBuiltIn.ShouldBeFalse();
                (await db.Set<TeamMember>().CountAsync(m => m.TeamId == team.Id)).ShouldBe(0);
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // ── C3: a space team wins over a same-named global team ─────────────────────────────────────

    [Theory]
    [InlineData(true, true, SpaceId)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, SpaceId)]
    public async Task Import_TeamNameMatchingGlobalAndOrSpaceTeam_ReusesTheClosestScopeDeterministically(bool spaceTeamExists, bool globalTeamExists, int expectedSpaceId)
    {
        var archivePath = WriteArchive(Documents(teams: [("Teams-1", "Ops")], manualTeamIds: "Teams-1"));

        try
        {
            await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
            {
                if (globalTeamExists) await repository.InsertAsync(new Team { Name = "Ops", SpaceId = 0 });
                if (spaceTeamExists) await repository.InsertAsync(new Team { Name = "ops", SpaceId = SpaceId });
                await repository.InsertAsync(new Team { Name = "Ops", SpaceId = OtherSpaceId });
                await unitOfWork.SaveChangesAsync();
            });

            var result = await ImportAsInternalUserAsync(archivePath);

            result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(result));

            await Run<SquidDbContext>(async db =>
            {
                var expected = await db.Set<Team>().SingleAsync(t => t.SpaceId == expectedSpaceId);
                (await ManualResponsibleTeamIdsAsync(db)).ShouldBe(expected.Id.ToString());
                (await db.Set<Team>().CountAsync()).ShouldBe((spaceTeamExists ? 1 : 0) + (globalTeamExists ? 1 : 0) + 1, customMessage: "reuse never creates another team");
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // ── C4: reusing a team grants approval to its current members → warn, and detect drift ────

    [Fact]
    public async Task Preview_ReusingAnExistingTeam_WarnsWithTheTeamAndItsMemberCount()
    {
        var archivePath = WriteArchive(Documents(teams: [("Teams-1", "Approvers")], manualTeamIds: "Teams-1"));

        try
        {
            await SeedTeamWithMembersAsync("Approvers", SpaceId, 3);

            await Run<IOctopusImportPlanningPipeline>(async pipeline =>
            {
                var snapshot = await pipeline.BuildPreviewAsync(archivePath, SpaceId);
                var team = snapshot.PreviewPlan.Resources.Single(r => r.SourceId.StartsWith("Teams-1"));

                team.PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting);
                team.DestinationMemberCount.ShouldBe(3);

                var warning = team.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.ReusedTeamGrantsApproval);
                warning.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
                warning.Message.ShouldContain("'Approvers'");
                warning.Message.ShouldContain("3 member(s)");
            });
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Theory]
    [InlineData("member-added")]
    [InlineData("member-removed")]
    [InlineData("member-swapped")]
    [InlineData("team-edited")]
    [InlineData("team-deleted")]
    public async Task Confirm_WhenReusedTeamChangesBetweenPreviewAndConfirm_IsBlockedAsStaleAndWritesNothing(string change)
    {
        var archivePath = WriteArchive(Documents(teams: [("Teams-1", "Approvers")], manualTeamIds: "Teams-1"));

        try
        {
            var teamId = await SeedTeamWithMembersAsync("Approvers", SpaceId, 2);
            var sessionId = Guid.NewGuid();

            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                db.Set<OctopusImportSession>().Add(Session(sessionId, OctopusImportSessionState.Validated, CurrentUsers.InternalUser.Id));
                await db.SaveChangesAsync();

                var snapshot = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);
                snapshot.PreviewPlan.HasBlockers.ShouldBeFalse();

                await Task.Delay(20);
                await ApplyTeamChangeAsync(db, teamId, change);

                var result = await scope.Resolve<IOctopusImportConfirmationOrchestrator>().ConfirmAsync(new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan));

                result.State.ShouldBe(OctopusImportSessionState.Failed);
                result.Result.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan || d.Code == OctopusImportPreviewDiagnosticCodes.IncompatibleSharedResourceReuse);
            });

            await AssertNothingImportedAsync(expectedTeams: change == "team-deleted" ? 0 : 1);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Confirm_ReusingTheBuiltInEveryoneTeam_IsNotStaleWhenAUserIsCreatedBetweenPreviewAndConfirm()
    {
        var archivePath = WriteArchive(Documents(teams: [("teams-everyone", "Everyone")], manualTeamIds: "teams-everyone", uniqueIds: false));

        try
        {
            var everyoneId = (await SeedSquidBuiltInTeamsAsync())[BuiltInTeamNames.Everyone];
            var sessionId = Guid.NewGuid();

            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                db.Set<TeamMember>().Add(new TeamMember { TeamId = everyoneId, UserId = 9001 });
                db.Set<OctopusImportSession>().Add(Session(sessionId, OctopusImportSessionState.Validated, CurrentUsers.InternalUser.Id));
                await db.SaveChangesAsync();

                var snapshot = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);
                var team = snapshot.PreviewPlan.Resources.Single(r => r.SourceId == "teams-everyone");
                team.DestinationMemberCount.ShouldBeNull();
                team.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ReusedTeamGrantsApproval && d.Message.Contains("every user"));

                // AccountService adds every new user to Everyone: that must not invalidate the plan.
                db.Set<TeamMember>().Add(new TeamMember { TeamId = everyoneId, UserId = 9002 });
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                var result = await scope.Resolve<IOctopusImportConfirmationOrchestrator>().ConfirmAsync(new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan));

                result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(result));
                result.Result.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.StalePreviewPlan);
            });

            await Run<SquidDbContext>(async db => (await ManualResponsibleTeamIdsAsync(db)).ShouldBe(everyoneId.ToString()));
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // ── C5: an unmapped responsible team is fail-closed and writes NOTHING ─────────────────────

    [Fact]
    public async Task Confirm_ResponsibleTeamMissingFromTheExport_IsBlockedAndWritesNothing()
    {
        var archivePath = WriteArchive(Documents(teams: [], manualTeamIds: "Teams-404"));

        try
        {
            var sessionId = Guid.NewGuid();

            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                db.Set<OctopusImportSession>().Add(Session(sessionId, OctopusImportSessionState.Validated, CurrentUsers.InternalUser.Id));
                await db.SaveChangesAsync();

                var snapshot = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);
                var result = await scope.Resolve<IOctopusImportConfirmationOrchestrator>().ConfirmAsync(new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan));

                result.State.ShouldBe(OctopusImportSessionState.Failed);
                result.Result.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.MissingResponsibleTeam);
            });

            await AssertNothingImportedAsync();
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task Confirm_ResponsibleTeamNotMappedAtConfirmTime_AbortsMidTransactionAndRollsEverythingBack()
    {
        var archivePath = WriteArchive(Documents(teams: [("Teams-1", "Approvers")], manualTeamIds: "Teams-1"));

        try
        {
            var sessionId = Guid.NewGuid();

            await Run<ILifetimeScope>(async scope =>
            {
                var db = scope.Resolve<SquidDbContext>();
                db.Set<OctopusImportSession>().Add(Session(sessionId, OctopusImportSessionState.Validated, CurrentUsers.InternalUser.Id));
                await db.SaveChangesAsync();

                var snapshot = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);
                snapshot.PreviewPlan.HasBlockers.ShouldBeFalse();

                // A validated plan that (by tampering or drift) no longer creates the team: the process step
                // runs AFTER project group / environment / lifecycle / project rows were written, so the whole
                // transaction must roll back rather than leave a half-imported project without its approver.
                var team = snapshot.PreviewPlan.Resources.Single(r => r.SourceType == nameof(Squid.Core.Services.OctopusImport.Octopus.OctopusResourceKind.Team));
                team.PreviewAction = OctopusImportPreviewAction.Skip;

                var result = await scope.Resolve<IOctopusImportConfirmationOrchestrator>().ConfirmAsync(new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan));

                result.State.ShouldBe(OctopusImportSessionState.Failed);
                result.Result.Diagnostics.ShouldContain(d => d.Code == Squid.Core.Services.OctopusImport.Mapping.Actions.OctopusImportActionMappingDiagnosticCodes.MissingResponsibleTeamMapping);
                result.Result.Diagnostics.ShouldContain(d => d.Code == OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack);
            });

            await AssertNothingImportedAsync();
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // ── Drivers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<OctopusImportSessionDto> ImportAsInternalUserAsync(string archivePath)
    {
        OctopusImportSessionDto result = null;

        await Run<ILifetimeScope>(async scope =>
        {
            var sessionId = Guid.NewGuid();
            var db = scope.Resolve<SquidDbContext>();
            db.Set<OctopusImportSession>().Add(Session(sessionId, OctopusImportSessionState.Validated, CurrentUsers.InternalUser.Id));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var snapshot = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);
            snapshot.PreviewPlan.HasBlockers.ShouldBeFalse(customMessage: string.Join("; ", snapshot.PreviewPlan.Resources.SelectMany(r => r.Diagnostics).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => $"{d.Code}:{d.SourceId}")));

            result = await scope.Resolve<IOctopusImportConfirmationOrchestrator>().ConfirmAsync(new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan));
        });

        return result;
    }

    private async Task<Guid> SeedValidatedSessionAsync(string archivePath)
    {
        var sessionId = Guid.NewGuid();

        await Run<ILifetimeScope>(async scope =>
        {
            var snapshot = await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);
            snapshot.PreviewPlan.HasBlockers.ShouldBeFalse();

            var session = Session(sessionId, OctopusImportSessionState.Validated, ImporterUserId);
            session.TemporaryUploadPath = archivePath;
            session.ValidatedPlanJson = JsonSerializer.Serialize(new OctopusImportValidatedPlanDto { PreviewPlan = snapshot.PreviewPlan, Validation = new OctopusImportValidationResultDto() }, PlanJsonOptions);

            var db = scope.Resolve<SquidDbContext>();
            db.Set<OctopusImportSession>().Add(session);
            await db.SaveChangesAsync();
        });

        return sessionId;
    }

    private async Task<ConfirmOctopusImportResponse> ConfirmAsImporterAsync(Guid sessionId)
    {
        ConfirmOctopusImportResponse response = null;

        await Run<IMediator>(
            async mediator => response = await mediator.SendAsync<ConfirmOctopusImportCommand, ConfirmOctopusImportResponse>(new ConfirmOctopusImportCommand { SpaceId = SpaceId, SessionId = sessionId }),
            builder => builder.RegisterInstance<ICurrentUser>(new FixedCurrentUser(ImporterUserId)).SingleInstance());

        return response;
    }

    private async Task SeedSpaceScopedImporterAsync(IReadOnlyCollection<Permission> permissions, int roleSpaceId = SpaceId)
    {
        await Run<ITeamDataProvider, IUserRoleDataProvider, IScopedUserRoleDataProvider>(async (teams, roles, scopedRoles) =>
        {
            var team = new Team { Name = "Importers", SpaceId = roleSpaceId };
            await teams.AddAsync(team);
            await teams.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = ImporterUserId });

            var role = new UserRole { Name = "Space importer", IsBuiltIn = false };
            await roles.AddAsync(role);
            await roles.SetPermissionsAsync(role.Id, permissions.Select(p => p.ToString()).ToList());

            await scopedRoles.AddAsync(new ScopedUserRole { TeamId = team.Id, UserRoleId = role.Id, SpaceId = roleSpaceId });
        });
    }

    private async Task<Dictionary<string, int>> SeedSquidBuiltInTeamsAsync()
    {
        var ids = new Dictionary<string, int>();

        await Run<ITeamDataProvider>(async teams =>
        {
            foreach (var name in new[] { BuiltInTeamNames.Administrators, BuiltInTeamNames.Everyone })
            {
                var team = new Team { Name = name, Description = "built-in", SpaceId = 0, IsBuiltIn = true };
                await teams.AddAsync(team);
                ids[name] = team.Id;
            }
        });

        return ids;
    }

    private async Task<int> SeedTeamWithMembersAsync(string name, int spaceId, int memberCount)
    {
        var teamId = 0;

        await Run<ITeamDataProvider>(async teams =>
        {
            var team = new Team { Name = name, SpaceId = spaceId };
            await teams.AddAsync(team);
            teamId = team.Id;

            for (var i = 1; i <= memberCount; i++)
                await teams.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = 9000 + i });
        });

        return teamId;
    }

    private static async Task ApplyTeamChangeAsync(SquidDbContext db, int teamId, string change)
    {
        switch (change)
        {
            case "member-added":
                db.Set<TeamMember>().Add(new TeamMember { TeamId = teamId, UserId = 9999 });
                break;
            case "member-removed":
                await db.Set<TeamMember>().Where(m => m.TeamId == teamId && m.UserId == 9001).ExecuteDeleteAsync();
                break;
            case "member-swapped":
                await db.Set<TeamMember>().Where(m => m.TeamId == teamId && m.UserId == 9001).ExecuteDeleteAsync();
                db.Set<TeamMember>().Add(new TeamMember { TeamId = teamId, UserId = 9999 });
                break;
            case "team-edited":
                (await db.Set<Team>().SingleAsync(t => t.Id == teamId)).Description = "changed after preview";
                break;
            case "team-deleted":
                await db.Set<TeamMember>().Where(m => m.TeamId == teamId).ExecuteDeleteAsync();
                await db.Set<Team>().Where(t => t.Id == teamId).ExecuteDeleteAsync();
                break;
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private async Task AssertNothingImportedAsync(int expectedTeams = 0)
    {
        await Run<SquidDbContext>(async db =>
        {
            (await db.Set<ProjectGroup>().CountAsync(x => x.SpaceId == SpaceId)).ShouldBe(0);
            (await db.Set<Project>().CountAsync(x => x.SpaceId == SpaceId)).ShouldBe(0);
            (await db.Set<SquidEnvironment>().CountAsync(x => x.SpaceId == SpaceId)).ShouldBe(0);
            (await db.Set<Lifecycle>().CountAsync(x => x.SpaceId == SpaceId)).ShouldBe(0);
            (await db.Set<Channel>().CountAsync()).ShouldBe(0);
            (await db.Set<VariableSet>().CountAsync()).ShouldBe(0);
            (await db.Set<Variable>().CountAsync()).ShouldBe(0);
            (await db.Set<LibraryVariableSet>().CountAsync()).ShouldBe(0);
            (await db.Set<ExternalFeed>().CountAsync()).ShouldBe(0);
            (await db.Set<DeploymentAccount>().CountAsync()).ShouldBe(0);
            (await db.Set<DeploymentProcess>().CountAsync()).ShouldBe(0);
            (await db.Set<DeploymentStep>().CountAsync()).ShouldBe(0);
            (await db.Set<DeploymentAction>().CountAsync()).ShouldBe(0);
            (await db.Set<DeploymentActionProperty>().CountAsync()).ShouldBe(0);
            (await db.Set<Release>().CountAsync()).ShouldBe(0);
            (await db.Set<Team>().CountAsync(x => x.SpaceId == SpaceId && x.Name != "Importers")).ShouldBe(expectedTeams);
        });
    }

    private async Task AssertSessionStateAsync(Guid sessionId, OctopusImportSessionState expected)
    {
        await Run<SquidDbContext>(async db =>
            (await db.Set<OctopusImportSession>().AsNoTracking().SingleAsync(s => s.SessionId == sessionId)).State.ShouldBe(expected.ToString()));
    }

    private static async Task<string> ManualResponsibleTeamIdsAsync(SquidDbContext db)
    {
        var action = await db.Set<DeploymentAction>().SingleAsync(a => a.Name == "Approval");

        return await db.Set<DeploymentActionProperty>().Where(p => p.ActionId == action.Id && p.PropertyName == SpecialVariables.Action.ManualResponsibleTeamIds).Select(p => p.PropertyValue).SingleOrDefaultAsync();
    }

    private static List<Permission> SpaceOwnerPermissions() => BuiltInRoles.SpaceOwner.Permissions.ToList();

    private static string Describe(OctopusImportSessionDto result)
        => string.Join("; ", (result.Result?.Diagnostics ?? []).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => $"{d.Code}:{d.SourceId}:{d.Message}"));

    private static OctopusImportSession Session(Guid sessionId, OctopusImportSessionState state, int ownerUserId)
        => new()
        {
            SessionId = sessionId,
            DestinationSpaceId = SpaceId,
            OwnerUserId = ownerUserId,
            State = state.ToString(),
            SourceSummaryJson = "{}",
            DataVersion = Guid.NewGuid().ToByteArray(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastStateChangedAt = DateTimeOffset.UtcNow
        };

    // ── Export documents ────────────────────────────────────────────────────────────────────────

    private static List<(string Id, string DocumentType, string DocumentSource, string Json)> Documents(IReadOnlyList<(string Id, string Name)> teams, string manualTeamIds, bool uniqueIds = true)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string Id(string prefix) => $"{prefix}-{suffix}";
        string TeamId(string id) => uniqueIds ? Id(id) : id;

        var approval = new
        {
            Id = Id("Steps-approval"),
            Name = "Approval",
            Condition = "Success",
            StartTrigger = "StartAfterPrevious",
            PackageRequirement = "LetOctopusDecide",
            Properties = new Dictionary<string, string>(),
            Actions = new[]
            {
                new
                {
                    Id = Id("Actions-approval"), Name = "Approval", ActionType = "Octopus.Manual", IsRequired = false, IsDisabled = false,
                    Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(),
                    Properties = new Dictionary<string, string>
                    {
                        ["Octopus.Action.Manual.Instructions"] = "Approve the release",
                        ["Octopus.Action.Manual.ResponsibleTeamIds"] = string.Join(",", manualTeamIds.Split(',').Select(TeamId)),
                        ["Octopus.Action.RunOnServer"] = "true"
                    }
                }
            }
        };

        var documents = new List<(string Id, string DocumentType, string DocumentSource, string Json)>
        {
            Doc(Id("ProjectGroups-1"), "ProjectGroup", new { Id = Id("ProjectGroups-1"), Name = $"Group {suffix}", Slug = $"group-{suffix}" }),
            Doc(Id("Environments-1"), "StaticDeploymentEnvironment", new { Id = Id("Environments-1"), Name = $"Env {suffix}", Slug = $"env-{suffix}" }),
            Doc(Id("Lifecycles-1"), "Lifecycle", new { Id = Id("Lifecycles-1"), Name = $"Lifecycle {suffix}", Slug = $"lc-{suffix}", Phases = new[] { new { Id = Id("Phases-1"), Name = "Only", AutomaticDeploymentTargets = new[] { Id("Environments-1") }, OptionalDeploymentTargets = Array.Empty<string>() } } }),
            Doc(Id("Projects-1"), "Project", new { Id = Id("Projects-1"), Name = $"Project {suffix}", Slug = $"project-{suffix}", ProjectGroupId = Id("ProjectGroups-1"), LifecycleId = Id("Lifecycles-1"), VariableSetId = Id("variableset-Projects-1"), DeploymentProcessId = Id("deploymentprocess-Projects-1"), IncludedLibraryVariableSetIds = Array.Empty<string>() }),
            Doc(Id("Channels-1"), "Channel", new { Id = Id("Channels-1"), Name = "Default", ProjectId = Id("Projects-1"), LifecycleId = (string)null, IsDefault = true, Slug = "default" }),
            Doc(Id("variableset-Projects-1"), "ProjectVariables", new { Id = Id("variableset-Projects-1"), OwnerId = Id("Projects-1"), OwnerType = "Project", Variables = Array.Empty<object>() }),
            Doc(Id("deploymentprocess-Projects-1"), "DeploymentProcess", new { Id = Id("deploymentprocess-Projects-1"), OwnerId = Id("Projects-1"), Steps = new[] { approval } })
        };

        foreach (var (id, name) in teams)
            documents.Add(Doc(TeamId(id), "ConfigurableTeam", new { Id = TeamId(id), Name = name, Description = "", MemberUserIds = new[] { "Users-1" }, ExternalSecurityGroups = Array.Empty<string>() }));

        return documents;
    }

    private static (string Id, string DocumentType, string DocumentSource, string Json) Doc(string id, string documentType, object document)
        => (id, documentType, $"{id}.json", JsonSerializer.Serialize(document));

    private static string WriteArchive(IReadOnlyList<(string Id, string DocumentType, string DocumentSource, string Json)> documents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-teams-{Guid.NewGuid():N}.zip");
        var manifest = JsonSerializer.Serialize(new
        {
            SchemaVersions = Array.Empty<string>(),
            Entries = documents.Select(d => new { d.Id, Name = d.Id, d.DocumentType, ExportType = "FullDocument", d.DocumentSource, ParentId = (string)null, Hash = Sha1(d.Json) })
        });

        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        WriteEntry(archive, "manifest.json", manifest);

        foreach (var document in documents)
            WriteEntry(archive, document.DocumentSource, document.Json);

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string Sha1(string value) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FixedCurrentUser(int id) : ICurrentUser
    {
        public int? Id => id;
        public string Name => "space-scoped-importer";
        public bool IsInternal => false;
    }
}
