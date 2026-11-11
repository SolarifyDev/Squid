using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;
using SquidEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier for library variable set imports on a REAL isolated Postgres database: preview and
/// confirmation run through the real planning pipeline and orchestrator. Pins that preview predicts what
/// confirmation writes, that a reused library is never rewritten, that concurrent confirmations cannot
/// duplicate a library, and the runtime precedence the preview warning describes.
/// </summary>
public class OctopusImportLibraryVariableSetIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private const string ExportPassword = "fake-lvs-export-password";

    public OctopusImportLibraryVariableSetIntegrationTests()
        : base("OctopusImportLibraryVariableSets", "squid_it_octopus_import_library_variable_sets")
    {
    }

    // ── B1: a reused library never blocks and is never rewritten ─────────────────────────────────

    [Fact]
    public async Task ReusedLibrary_WithUndecryptableSecretAndUnmappableScopes_ImportsWithoutPassword_AndKeepsTheLibraryUntouched()
    {
        var libraryName = Unique("Shared Lib");
        var first = new Export().Library("L", libraryName, Var("Issuer", "https://issuer.first")).Project("P", Unique("First"), ["L"]);
        await Run<ILifetimeScope>(async scope => (await ImportAsync(scope, first, OctopusExportSecrets.FromPassword(ExportPassword))).State.ShouldBe(OctopusImportSessionState.Succeeded));

        var second = new Export().Machine("M", "web-1", "web");
        second.Library("L", libraryName, Var("Issuer", "CHANGED"), Secret("Token", "never-decrypted"), Var("PerMachine", "m", Scope("Machine", second.Id("M"))), Var("PerRole", "r", Scope("Role", "db")), Var("PerTenant", "t", Scope("TenantTag", "Tags/VIP")))
            .Project("P", Unique("Second"), ["L"]);

        await Run<ILifetimeScope>(async scope =>
        {
            var db = scope.Resolve<SquidDbContext>();
            var snapshot = await PlanAsync(scope, second, OctopusExportSecrets.None);

            snapshot.PreviewPlan.HasBlockers.ShouldBeFalse(customMessage: Blockers(snapshot.PreviewPlan));
            var result = await ConfirmAsync(scope, db, snapshot);

            result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(result));
            var library = await db.Set<LibraryVariableSet>().AsNoTracking().SingleAsync(l => l.SpaceId == SpaceId && l.Name == libraryName);
            (await db.Set<Variable>().AsNoTracking().Where(v => v.VariableSetId == library.VariableSetId).Select(v => v.Name + "=" + v.Value).ToListAsync()).ShouldBe(["Issuer=https://issuer.first"]);
            var libraryVariables = result.Result.Resources.Where(r => r.SourceType == nameof(OctopusResourceKind.Variable) && r.SourceId.StartsWith("var-L-") && r.SourceId.EndsWith(second.Suffix)).ToList();
            libraryVariables.Count.ShouldBe(5);
            libraryVariables.ShouldAllBe(r => r.OutcomeState == OctopusImportResourceOutcomeState.Skipped);
            result.Result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportConfirmationDiagnosticCodes.ReusedLibraryVariableSetVariablesKept);
        });
    }

    // ── B2: scopes preview allows are written faithfully; scopes it blocks write nothing ─────────

    [Theory]
    [InlineData("Environment", true)]
    [InlineData("Role", true)]
    [InlineData("ProjectChannel", true)]
    [InlineData("ProjectAction", true)]
    [InlineData("Machine", false)]
    [InlineData("TenantTag", false)]
    [InlineData("MissingEnvironment", false)]
    [InlineData("LibraryChannel", false)]
    public async Task ScopedVariables_PreviewVerdictMatchesWhatConfirmationWrites(string scopeCase, bool previewAllows)
    {
        var export = new Export().Machine("M", "web-1", "web");
        var (scope, onProject) = ScopeFor(scopeCase, export);
        var variable = Var("Scoped", "v", scope);
        var projectName = Unique("Scoped");
        export.Library("L", Unique("Scoped Lib"), onProject ? [] : [variable]).Project("P", projectName, ["L"], onProject ? [variable] : []);

        await Run<ILifetimeScope>(async lifetime =>
        {
            var db = lifetime.Resolve<SquidDbContext>();
            var snapshot = await PlanAsync(lifetime, export, OctopusExportSecrets.None);
            var validation = lifetime.Resolve<IOctopusImportPreviewValidator>().Validate(snapshot.Graph, snapshot.DependencyPlan, snapshot.Conflicts, snapshot.PreviewPlan);

            validation.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.UnmappableVariableScope).ShouldBe(!previewAllows);

            var result = await ConfirmAsync(lifetime, db, snapshot);

            result.State.ShouldBe(previewAllows ? OctopusImportSessionState.Succeeded : OctopusImportSessionState.Failed, customMessage: Describe(result));
            (await db.Set<Project>().AnyAsync(p => p.Name == projectName)).ShouldBe(previewAllows);

            if (!previewAllows) return;

            var project = await db.Set<Project>().AsNoTracking().SingleAsync(p => p.Name == projectName);
            var libraryId = project.GetIncludedLibraryVariableSetIdList().Single();
            var variableSetId = onProject ? project.VariableSetId : (await db.Set<LibraryVariableSet>().AsNoTracking().SingleAsync(l => l.Id == libraryId)).VariableSetId;
            var written = await db.Set<Variable>().AsNoTracking().SingleAsync(v => v.Name == "Scoped" && v.VariableSetId == variableSetId);
            (await db.Set<VariableScope>().CountAsync(s => s.VariableId == written.Id)).ShouldBe(1, customMessage: "an allowed scope must be persisted, never dropped");
        });
    }

    // ── B4: pin the runtime precedence the preview warning describes ─────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectAndLibraryDefineTheSameName_PreviewWarns_AndRuntimeResolvesTheLibraryValue(bool scopedToTest)
    {
        var export = new Export();
        var scope = scopedToTest ? Scope("Environment", export.Id("Environments-TEST")) : null;
        var projectName = Unique("Precedence");
        export.Library("L", Unique("Precedence Lib"), Var("ConnStr", "library-value", scope)).Project("P", projectName, ["L"], [Var("ConnStr", "project-value", scope)]);

        await Run<ILifetimeScope>(async lifetime =>
        {
            var snapshot = await PlanAsync(lifetime, export, OctopusExportSecrets.None);

            snapshot.PreviewPlan.Resources.SelectMany(r => r.Diagnostics).ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ProjectVariableShadowedByLibraryVariable && d.Severity == OctopusImportCompatibilitySeverity.Warning);
            (await ConfirmAsync(lifetime, lifetime.Resolve<SquidDbContext>(), snapshot)).State.ShouldBe(OctopusImportSessionState.Succeeded);
        });

        await Run<SquidDbContext, IReleaseService, IDeploymentService, IDeploymentVariableResolver>(async (db, releases, deployments, resolver) =>
        {
            var project = await db.Set<Project>().SingleAsync(p => p.Name == projectName);
            var environment = await db.Set<SquidEnvironment>().SingleAsync(e => e.SpaceId == SpaceId && e.Name == export.EnvironmentName);
            var channel = await db.Set<Channel>().SingleAsync(c => c.ProjectId == project.Id && c.IsDefault);
            var release = await releases.CreateReleaseAsync(new CreateReleaseCommand { SpaceId = SpaceId, ProjectId = project.Id, ChannelId = channel.Id, Version = "1.0.0" });
            var deployment = await deployments.CreateDeploymentAsync(new CreateDeploymentCommand { SpaceId = SpaceId, ReleaseId = release.Release.Id, EnvironmentId = environment.Id, Name = "Precedence" });

            var variables = await resolver.ResolveVariablesAsync(deployment.Deployment.Id, CancellationToken.None);
            var effective = VariableScopeEvaluator.Evaluate(variables, new VariableScopeContext { EnvironmentId = environment.Id, EnvironmentName = environment.Name });

            effective.Single(v => v.Name == "ConnStr").Value.ShouldBe("library-value", customMessage: "the preview warning says Squid uses the LIBRARY value at equal scope; if runtime precedence changes, update the warning");
        });
    }

    // ── B6: concurrent confirmations cannot duplicate a library ──────────────────────────────────

    [Fact]
    public async Task NameLock_SecondTransactionWaitsForTheFirstToCommit_ThenSeesItsLibrary()
    {
        var name = Unique("Locked Lib");
        var firstLocked = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        int? createdId = null;

        var first = Run<SquidDbContext, IOctopusImportLibraryVariableSetNameLock>(async (db, nameLock) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            (await nameLock.LockNameAndFindExistingAsync(SpaceId, name)).ShouldBeNull();
            firstLocked.SetResult();
            await releaseFirst.Task;

            var library = new LibraryVariableSet { Name = name, SpaceId = SpaceId, ContentType = "Variables", Json = "{}" };
            db.Set<LibraryVariableSet>().Add(library);
            await db.SaveChangesAsync();
            createdId = library.Id;
            await transaction.CommitAsync();
        });

        await firstLocked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = LockInOwnTransactionAsync(SpaceId, $"  {name.ToUpperInvariant()} ");

        await Task.Delay(TimeSpan.FromSeconds(1));
        second.IsCompleted.ShouldBeFalse("the second caller must block on the advisory lock while the first transaction is open");

        releaseFirst.SetResult();
        await first;

        (await second.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBe(createdId, customMessage: "after the first commit the second caller must see the committed library — check pg_locks if this times out");
    }

    [Fact]
    public async Task NameLock_DifferentNameOrSpace_DoesNotBlock()
    {
        var name = Unique("Free Lib");
        var holderLocked = new TaskCompletionSource();
        var releaseHolder = new TaskCompletionSource();

        var holder = Run<SquidDbContext, IOctopusImportLibraryVariableSetNameLock>(async (db, nameLock) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await nameLock.LockNameAndFindExistingAsync(SpaceId, name);
            holderLocked.SetResult();
            await releaseHolder.Task;
        });

        await holderLocked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            foreach (var (space, candidate) in new[] { (SpaceId, name + " other"), (SpaceId + 1, name) })
            {
                (await LockInOwnTransactionAsync(space, candidate).WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeNull();
            }
        }
        finally
        {
            releaseHolder.SetResult();
            await holder;
        }
    }

    [Fact]
    public async Task NameLock_OutsideATransaction_RefusesBecauseTheLockWouldBeReleasedImmediately()
    {
        await Run<IOctopusImportLibraryVariableSetNameLock>(async nameLock =>
            await Should.ThrowAsync<InvalidOperationException>(() => nameLock.LockNameAndFindExistingAsync(SpaceId, "anything")));
    }

    [Fact]
    public async Task TwoSessions_ConfirmingConcurrently_CreateTheSharedLibraryOnce_AndTheOtherFailsWithAClearDiagnostic()
    {
        var libraryName = Unique("Race Lib");
        var exports = new[] { new Export(), new Export() };
        var projectNames = new[] { Unique("Race A"), Unique("Race B") };

        for (var i = 0; i < exports.Length; i++)
            exports[i].Library("L", libraryName, Var("Issuer", $"from-{i}")).Project("P", projectNames[i], ["L"]);

        var snapshots = new OctopusImportPlanningSnapshot[2];

        for (var i = 0; i < exports.Length; i++)
        {
            var index = i;
            await Run<ILifetimeScope>(async lifetime => snapshots[index] = await PlanAsync(lifetime, exports[index], OctopusExportSecrets.None));
            snapshots[index].PreviewPlan.Resources.Single(r => r.SourceType == nameof(OctopusResourceKind.LibraryVariableSet)).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Run<ILifetimeScope, OctopusImportSessionDto>(lifetime => ConfirmAsync(lifetime, lifetime.Resolve<SquidDbContext>(), snapshots[i]))));

        results.Count(r => r.State == OctopusImportSessionState.Succeeded).ShouldBe(1, customMessage: string.Join(" | ", results.Select(Describe)));
        var loser = results.Single(r => r.State == OctopusImportSessionState.Failed);
        loser.Result.Diagnostics.ShouldContain(d => d.Code == OctopusImportConfirmationDiagnosticCodes.LibraryVariableSetCreatedConcurrently && d.Message.Contains(libraryName));

        await Run<SquidDbContext>(async db =>
        {
            (await db.Set<LibraryVariableSet>().CountAsync(l => l.SpaceId == SpaceId && l.Name == libraryName)).ShouldBe(1);
            (await db.Set<Project>().CountAsync(p => projectNames.Contains(p.Name))).ShouldBe(1, customMessage: "the losing confirmation must roll back completely");
        });
    }

    // ── B7: shared libraries, mixed create/reuse, stale and ambiguous reuse, role scopes ─────────

    [Fact]
    public async Task OneExport_TwoProjectsSharingLibraries_WithMixedCreateAndReuse_WritesEachLibraryOnceAndLinksInSourceOrder()
    {
        var existingName = Unique("Existing Lib");
        await SeedLibraryAsync(existingName, ("Keep", "destination-value"));

        var sharedName = Unique("Shared New");
        var onlyBName = Unique("Only B");
        var export = new Export()
            .Library("L1", existingName, Var("Keep", "source-value"))
            .Library("L2", sharedName, Var("Two", "2"), Secret("TwoSecret", "two-secret"))
            .Library("L3", onlyBName, Var("Three", "3"));
        var projectA = Unique("Mixed A");
        var projectB = Unique("Mixed B");
        export.Project("A", projectA, ["L1", "L2"]).Project("B", projectB, ["L2", "L3"]);

        await Run<ILifetimeScope>(async lifetime =>
        {
            var db = lifetime.Resolve<SquidDbContext>();
            var snapshot = await PlanAsync(lifetime, export, OctopusExportSecrets.FromPassword(ExportPassword));
            var result = await ConfirmAsync(lifetime, db, snapshot);

            result.State.ShouldBe(OctopusImportSessionState.Succeeded, customMessage: Describe(result));

            var libraries = await db.Set<LibraryVariableSet>().AsNoTracking().Where(l => l.SpaceId == SpaceId && (l.Name == existingName || l.Name == sharedName || l.Name == onlyBName)).ToListAsync();
            var l1 = libraries.Single(l => l.Name == existingName);
            var l2 = libraries.Single(l => l.Name == sharedName);
            var l3 = libraries.Single(l => l.Name == onlyBName);

            (await db.Set<Project>().SingleAsync(p => p.Name == projectA)).GetIncludedLibraryVariableSetIdList().ShouldBe([l1.Id, l2.Id]);
            (await db.Set<Project>().SingleAsync(p => p.Name == projectB)).GetIncludedLibraryVariableSetIdList().ShouldBe([l2.Id, l3.Id]);
            (await VariableValuesAsync(db, l1.VariableSetId)).ShouldBe(["Keep=destination-value"]);
            (await db.Set<Variable>().AsNoTracking().Where(v => v.VariableSetId == l2.VariableSetId).Select(v => v.Name).ToListAsync()).ShouldBe(["Two", "TwoSecret"], ignoreOrder: true);
            (await VariableValuesAsync(db, l3.VariableSetId)).ShouldBe(["Three=3"]);

            OutcomeOf(result, export.Id("var-L1-Keep")).ShouldBe(OctopusImportResourceOutcomeState.Skipped);
            OutcomeOf(result, export.Id("var-L2-Two")).ShouldBe(OctopusImportResourceOutcomeState.Created);
            OutcomeOf(result, export.Id("var-L3-Three")).ShouldBe(OctopusImportResourceOutcomeState.Created);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReusedLibrary_ChangedOrDeletedAfterPreview_FailsConfirmationWithoutWriting(bool deleteInsteadOfModify)
    {
        var libraryName = Unique("Stale Lib");
        var library = await SeedLibraryAsync(libraryName, ("Keep", "v1"));
        var projectName = Unique("Stale");
        var export = new Export().Library("L", libraryName, Var("Keep", "source")).Project("P", projectName, ["L"]);

        OctopusImportPlanningSnapshot snapshot = null;
        await Run<ILifetimeScope>(async lifetime => snapshot = await PlanAsync(lifetime, export, OctopusExportSecrets.None));
        snapshot.PreviewPlan.Resources.Single(r => r.SourceId == export.Id("L")).PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting);

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await Run<SquidDbContext, IVariableService>(async (db, variables) =>
        {
            if (deleteInsteadOfModify)
            {
                db.Set<LibraryVariableSet>().Remove(await db.Set<LibraryVariableSet>().SingleAsync(l => l.Id == library.Id));
                await db.SaveChangesAsync();
                return;
            }

            await variables.UpdateVariableSetAsync(new UpdateVariableSetCommand { Id = library.VariableSetId, Name = libraryName, OwnerId = library.Id, OwnerType = VariableSetOwnerType.LibraryVariableSet, SpaceId = SpaceId, Variables = [new VariableModel { Name = "Keep", Value = "v2", Type = VariableType.String }] }, CancellationToken.None);
            await db.SaveChangesAsync();
        });

        await Run<ILifetimeScope>(async lifetime =>
        {
            var db = lifetime.Resolve<SquidDbContext>();
            var result = await ConfirmAsync(lifetime, db, snapshot);

            result.State.ShouldBe(OctopusImportSessionState.Failed);
            result.Result.Diagnostics.Select(d => d.Code).ShouldContain(deleteInsteadOfModify ? OctopusImportPreviewDiagnosticCodes.IncompatibleSharedResourceReuse : OctopusImportPreviewDiagnosticCodes.StalePreviewPlan);
            (await db.Set<Project>().AnyAsync(p => p.Name == projectName)).ShouldBeFalse();
        });
    }

    [Fact]
    public async Task TwoDestinationLibrariesMatchingByName_PreviewRequiresARename_AndNothingIsWritten()
    {
        var libraryName = Unique("Ambiguous Lib");
        await SeedLibraryAsync(libraryName, ("A", "1"));
        await SeedLibraryAsync($"  {libraryName.ToUpperInvariant()} ", ("A", "2"));
        var projectName = Unique("Ambiguous");
        var export = new Export().Library("L", libraryName, Var("A", "source")).Project("P", projectName, ["L"]);

        await Run<ILifetimeScope>(async lifetime =>
        {
            var db = lifetime.Resolve<SquidDbContext>();
            var snapshot = await PlanAsync(lifetime, export, OctopusExportSecrets.None);

            snapshot.PreviewPlan.Resources.Single(r => r.SourceId == export.Id("L")).PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired);
            (await ConfirmAsync(lifetime, db, snapshot)).State.ShouldBe(OctopusImportSessionState.Failed);
            (await db.Set<Project>().AnyAsync(p => p.Name == projectName)).ShouldBeFalse();
        });
    }

    // Policy pin (pre-existing for project variables, now also for created libraries): a role-scoped value
    // whose role no exported target declares blocks, because Squid cannot tell a typo from a real role.
    [Fact]
    public async Task CreatedLibrary_RoleScopedVariableWithoutAnExportedMachine_BlocksAtPreview()
    {
        var export = new Export().Library("L", Unique("Role Lib"), Var("PerRole", "r", Scope("Role", "web"))).Project("P", Unique("Role"), ["L"]);

        await Run<ILifetimeScope>(async lifetime =>
        {
            var snapshot = await PlanAsync(lifetime, export, OctopusExportSecrets.None);
            var validation = lifetime.Resolve<IOctopusImportPreviewValidator>().Validate(snapshot.Graph, snapshot.DependencyPlan, snapshot.Conflicts, snapshot.PreviewPlan);

            validation.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.MissingTargetRole && d.SourceId == export.Id("var-L-PerRole"));
        });
    }

    [Fact]
    public async Task ProjectIncludingALibraryMissingFromTheExport_FailsConfirmationAndWritesNothing()
    {
        var projectName = Unique("Missing Lib");
        var export = new Export().Project("P", projectName, ["L-absent"]);

        await Run<ILifetimeScope>(async lifetime =>
        {
            var db = lifetime.Resolve<SquidDbContext>();
            var result = await ConfirmAsync(lifetime, db, await PlanAsync(lifetime, export, OctopusExportSecrets.None));

            result.State.ShouldBe(OctopusImportSessionState.Failed);
            result.Result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportPreviewDiagnosticCodes.UnresolvedReference);
            (await db.Set<Project>().AnyAsync(p => p.Name == projectName)).ShouldBeFalse();
        });
    }

    // ── Driver ───────────────────────────────────────────────────────────────────────────────────

    private Task<int?> LockInOwnTransactionAsync(int spaceId, string name)
        => Run<ILifetimeScope, int?>(async lifetime =>
        {
            await using var transaction = await lifetime.Resolve<SquidDbContext>().Database.BeginTransactionAsync();
            return await lifetime.Resolve<IOctopusImportLibraryVariableSetNameLock>().LockNameAndFindExistingAsync(spaceId, name);
        });

    private static async Task<OctopusImportSessionDto> ImportAsync(ILifetimeScope scope, Export export, OctopusExportSecrets secrets)
        => await ConfirmAsync(scope, scope.Resolve<SquidDbContext>(), await PlanAsync(scope, export, secrets));

    private static async Task<OctopusImportPlanningSnapshot> PlanAsync(ILifetimeScope scope, Export export, OctopusExportSecrets secrets)
    {
        var archivePath = export.WriteArchive();

        try
        {
            scope.Resolve<IOctopusExportSecretsAccessor>().Secrets = secrets;
            return await scope.Resolve<IOctopusImportPlanningPipeline>().BuildPreviewAsync(archivePath, SpaceId);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    private static async Task<OctopusImportSessionDto> ConfirmAsync(ILifetimeScope scope, SquidDbContext db, OctopusImportPlanningSnapshot snapshot)
    {
        var sessionId = Guid.NewGuid();
        db.Set<OctopusImportSession>().Add(new OctopusImportSession { SessionId = sessionId, DestinationSpaceId = SpaceId, OwnerUserId = CurrentUsers.InternalUser.Id, State = nameof(OctopusImportSessionState.Validated), SourceSummaryJson = "{}", DataVersion = Guid.NewGuid().ToByteArray(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), LastStateChangedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return await scope.Resolve<IOctopusImportConfirmationOrchestrator>().ConfirmAsync(new OctopusImportConfirmationRequest(sessionId, SpaceId, snapshot.Graph, snapshot.DependencyPlan, snapshot.PreviewPlan));
    }

    private async Task<LibraryVariableSet> SeedLibraryAsync(string name, params (string Name, string Value)[] variables)
    {
        LibraryVariableSet library = null;

        await Run<SquidDbContext, IVariableService>(async (db, service) =>
        {
            var created = await service.CreateVariableSetAsync(new CreateVariableSetCommand { Name = name, OwnerType = VariableSetOwnerType.LibraryVariableSet, SpaceId = SpaceId, Variables = variables.Select(v => new VariableModel { Name = v.Name, Value = v.Value, Type = VariableType.String }).ToList() }, CancellationToken.None);
            await db.SaveChangesAsync();
            library = await db.Set<LibraryVariableSet>().AsNoTracking().SingleAsync(l => l.Id == created.OwnerId);
        });

        return library;
    }

    private static async Task<List<string>> VariableValuesAsync(SquidDbContext db, int variableSetId)
        => await db.Set<Variable>().AsNoTracking().Where(v => v.VariableSetId == variableSetId).Select(v => v.Name + "=" + v.Value).ToListAsync();

    private static OctopusImportResourceOutcomeState OutcomeOf(OctopusImportSessionDto result, string sourceId)
        => result.Result.Resources.Single(r => r.SourceId == sourceId).OutcomeState;

    private static (Dictionary<string, string[]> Scope, bool OnProject) ScopeFor(string scopeCase, Export export)
        => scopeCase switch
        {
            "Environment" => (Scope("Environment", export.Id("Environments-TEST")), false),
            "Role" => (Scope("Role", "web"), false),
            "ProjectChannel" => (Scope("Channel", export.Id("Channels-P")), true),
            "ProjectAction" => (Scope("Action", export.Id("Actions-P")), true),
            "Machine" => (Scope("Machine", export.Id("M")), false),
            "TenantTag" => (Scope("TenantTag", "Tags/VIP"), false),
            "MissingEnvironment" => (Scope("Environment", "Environments-404"), false),
            _ => (Scope("Channel", export.Id("Channels-P")), false)
        };

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    private static string Blockers(OctopusImportPreviewPlanDto preview)
        => string.Join("; ", preview.Resources.SelectMany(r => r.Diagnostics).Concat(preview.Diagnostics).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => $"{d.Code}:{d.SourceId}"));

    private static string Describe(OctopusImportSessionDto result)
        => string.Join("; ", (result.Result?.Diagnostics ?? []).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => $"{d.Code}:{d.SourceId}:{d.Message}"));

    private static Dictionary<string, string[]> Scope(string key, params string[] values) => new() { [key] = values };

    private static ExportVariable Var(string name, string value, Dictionary<string, string[]> scope = null) => new(name, value, false, scope);

    private static ExportVariable Secret(string name, string plaintext) => new(name, Encrypt(plaintext), true, null);

    private sealed record ExportVariable(string Name, string Value, bool IsSensitive, Dictionary<string, string[]> Scope);

    /// <summary>A self-contained Octopus export: every id is suffixed so tests never collide in the shared database.</summary>
    private sealed class Export
    {
        private readonly List<(string Id, string DocumentType, string Json)> _documents = [];

        public Export()
        {
            Add(Id("ProjectGroups"), "ProjectGroup", new { Id = Id("ProjectGroups"), Name = $"Group {Suffix}", Slug = $"group-{Suffix}" });
            Add(Id("Environments-TEST"), "StaticDeploymentEnvironment", new { Id = Id("Environments-TEST"), Name = EnvironmentName, Slug = EnvironmentName.ToLowerInvariant() });
            Add(Id("Lifecycles"), "Lifecycle", new { Id = Id("Lifecycles"), Name = $"Lifecycle {Suffix}", Slug = $"lifecycle-{Suffix}", Phases = new[] { new { Id = Id("Phases"), Name = "Test", AutomaticDeploymentTargets = new[] { Id("Environments-TEST") }, OptionalDeploymentTargets = Array.Empty<string>() } } });
        }

        public string Suffix { get; } = Guid.NewGuid().ToString("N")[..8];

        public string EnvironmentName => $"TEST-{Suffix}";

        public string Id(string key) => $"{key}-{Suffix}";

        public Export Machine(string key, string name, params string[] roles)
            => Add(Id(key), "Machine", new { Id = Id(key), Name = $"{name}-{Suffix}", Roles = roles, EnvironmentIds = new[] { Id("Environments-TEST") } });

        public Export Library(string key, string name, params ExportVariable[] variables)
        {
            Add(Id(key), "LibraryVariableSet", new { Id = Id(key), Name = name, VariableSetId = Id($"variableset-{key}"), ContentType = "Variables" });

            return Add(Id($"variableset-{key}"), "LibraryVariables", new { Id = Id($"variableset-{key}"), OwnerId = Id(key), OwnerType = "LibraryVariableSet", Variables = variables.Select(v => Variable(key, v)).ToArray() });
        }

        public Export Project(string key, string name, string[] libraryKeys, ExportVariable[] variables = null)
        {
            Add(Id(key), "Project", new { Id = Id(key), Name = name, Slug = $"{key}-{Suffix}".ToLowerInvariant(), ProjectGroupId = Id("ProjectGroups"), LifecycleId = Id("Lifecycles"), VariableSetId = Id($"variableset-{key}"), DeploymentProcessId = Id($"deploymentprocess-{key}"), IncludedLibraryVariableSetIds = libraryKeys.Select(Id).ToArray() });
            Add(Id($"Channels-{key}"), "Channel", new { Id = Id($"Channels-{key}"), Name = "Default", ProjectId = Id(key), IsDefault = true, Slug = "default" });
            Add(Id($"variableset-{key}"), "ProjectVariables", new { Id = Id($"variableset-{key}"), OwnerId = Id(key), OwnerType = "Project", Variables = (variables ?? []).Select(v => Variable(key, v)).ToArray() });

            var action = new { Id = Id($"Actions-{key}"), Name = "Run", ActionType = "Octopus.Script", Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = new Dictionary<string, string> { ["Octopus.Action.Script.ScriptSource"] = "Inline", ["Octopus.Action.Script.Syntax"] = "Bash", ["Octopus.Action.Script.ScriptBody"] = "echo hi", ["Octopus.Action.RunOnServer"] = "true" } };
            var step = new { Id = Id($"Steps-{key}"), Name = "Run", Condition = "Success", StartTrigger = "StartAfterPrevious", PackageRequirement = "LetOctopusDecide", Properties = new Dictionary<string, string>(), Actions = new[] { action } };

            return Add(Id($"deploymentprocess-{key}"), "DeploymentProcess", new { Id = Id($"deploymentprocess-{key}"), OwnerId = Id(key), Steps = new[] { step } });
        }

        public string WriteArchive()
        {
            var path = Path.Combine(Path.GetTempPath(), $"squid-octopus-lvs-{Guid.NewGuid():N}.zip");
            var manifest = JsonSerializer.Serialize(new { SchemaVersions = Array.Empty<string>(), Entries = _documents.Select(d => new { d.Id, Name = d.Id, d.DocumentType, ExportType = "FullDocument", DocumentSource = $"{d.Id}.json", ParentId = (string)null, Hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(d.Json))).ToLowerInvariant() }) });

            using var file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);

            WriteEntry(archive, "manifest.json", manifest);

            foreach (var document in _documents)
                WriteEntry(archive, $"{document.Id}.json", document.Json);

            return path;
        }

        private object Variable(string ownerKey, ExportVariable variable)
            => new { Id = Id($"var-{ownerKey}-{variable.Name}"), variable.Name, variable.Value, Type = variable.IsSensitive ? "Sensitive" : "String", variable.IsSensitive, Scope = variable.Scope ?? new Dictionary<string, string[]>() };

        private Export Add(string id, string documentType, object document)
        {
            _documents.Add((id, documentType, JsonSerializer.Serialize(document)));
            return this;
        }

        private static void WriteEntry(ZipArchive archive, string path, string content)
        {
            using var stream = archive.CreateEntry(path).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    /// <summary>Independent reference implementation of the Octopus export cipher (not shared with production code).</summary>
    private static string Encrypt(string plaintext)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(ExportPassword, Encoding.UTF8.GetBytes("Octopuss"), 1_000, HashAlgorithmName.SHA1, 16);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = RandomNumberGenerator.GetBytes(16);

        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(plaintext);

        return $"{Convert.ToBase64String(encryptor.TransformFinalBlock(bytes, 0, bytes.Length))}|{Convert.ToBase64String(aes.IV)}";
    }
}
