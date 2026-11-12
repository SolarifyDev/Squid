using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Integration tier for the persistent Octopus import source map on a REAL isolated Postgres database: the migration's
/// backfill from already-succeeded sessions, idempotent upserts, stale-row filtering on load, the space-scoped advisory
/// lock, and the new nullable release_notes column.
/// </summary>
public class OctopusImportSourceMapIntegrationTests : TestBase
{
    private const int SpaceId = 7;
    private const string MigrationFile = "20261113_add_octopus_import_source_map.sql";

    // Same shape OctopusImportSessionService writes into result_json (camelCase, string enums).
    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public OctopusImportSourceMapIntegrationTests()
        : base("OctopusImportSourceMap", "squid_it_octopus_import_source_map")
    {
    }

    // ── Backfill ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Backfill_FromSucceededSessions_NewestWins_RedactedNameIgnored()
    {
        var older = DateTimeOffset.UtcNow.AddDays(-2);
        var newer = DateTimeOffset.UtcNow.AddDays(-1);

        await Run<SquidDbContext>(async db =>
        {
            await InsertSessionAsync(db, SpaceId, "Succeeded", older, Mapping("Projects-1", "Project", 10, "Created", "Old name"), Mapping("Releases-1", "Release", 20, "Created", "1.0.0"));
            await InsertSessionAsync(db, SpaceId, "Succeeded", newer, Mapping(" projects-1 ", "Project", 11, "Reused", "[redacted]"), Mapping("Feeds-1", "Feed", 30, "Reused", "Docker"));
            await InsertSessionAsync(db, SpaceId + 1, "Succeeded", older, Mapping("Projects-1", "Project", 99, "Created", "Other space"));

            await RunMigrationAsync(db);

            var rows = await LoadRawRowsAsync(db);
            rows.Select(r => $"{r.SpaceId}|{r.SourceType}|{r.SourceId}|{r.DestinationId}|{r.OutcomeState}").ShouldBe([
                $"{SpaceId}|Feed|FEEDS-1|30|Reused",
                $"{SpaceId}|Project|PROJECTS-1|11|Reused",
                $"{SpaceId}|Release|RELEASES-1|20|Created",
                $"{SpaceId + 1}|Project|PROJECTS-1|99|Created"], ignoreOrder: true);
            rows.ShouldAllBe(r => r.Fingerprint == null && r.DetailFingerprint == null && r.LastSessionId != null);
        });
    }

    [Fact]
    public async Task Backfill_SkipsFailedSessions()
    {
        await Run<SquidDbContext>(async db =>
        {
            await InsertSessionAsync(db, SpaceId, "Failed", DateTimeOffset.UtcNow, Mapping("Projects-1", "Project", 10, "Created", "P"));
            await InsertSessionAsync(db, SpaceId, "Importing", DateTimeOffset.UtcNow, Mapping("Projects-2", "Project", 12, "Created", "P2"));
            await InsertSessionAsync(db, SpaceId, "Succeeded", DateTimeOffset.UtcNow, Mapping("Projects-3", "Project", 13, "Failed", "P3"), Mapping("Projects-4", "Project", 0, "Created", "P4"));

            await RunMigrationAsync(db);

            (await LoadRawRowsAsync(db)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Backfill_ReRun_NeverOverwritesALaterConfirmation()
    {
        await Run<SquidDbContext, IOctopusImportSourceMapDataProvider>(async (db, provider) =>
        {
            await InsertSessionAsync(db, SpaceId, "Succeeded", DateTimeOffset.UtcNow, Mapping("Releases-1", "Release", 20, "Created", "1.0.0"));
            await provider.UpsertAsync([Row("Release", "Releases-1", 20, "Created", "fp-1")], Guid.NewGuid());

            await RunMigrationAsync(db);

            (await LoadRawRowsAsync(db)).Single().Fingerprint.ShouldBe("fp-1");
        });
    }

    // ── Upsert ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_IsIdempotent()
    {
        var rows = new List<OctopusImportSourceMapRow>
        {
            Row("Release", "Releases-1", 20, "Created", "fp-r"),
            Row("Deployment", "Deployments-1", 30, "Created", "fp-d", "fp-details"),
            Row("Project", "projects-1", 10, "Reused")
        };
        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext, IOctopusImportSourceMapDataProvider>(async (db, provider) =>
        {
            await provider.UpsertAsync(rows, sessionId);
            var first = Describe(await LoadRawRowsAsync(db));

            await provider.UpsertAsync(rows, sessionId);
            var second = Describe(await LoadRawRowsAsync(db));

            second.ShouldBe(first);
            second.Count.ShouldBe(3);
            second.ShouldContain($"{SpaceId}|Project|PROJECTS-1|10|Reused||");
            second.ShouldContain($"{SpaceId}|Deployment|DEPLOYMENTS-1|30|Created|fp-d|fp-details");
        });
    }

    [Fact]
    public async Task Upsert_ReRecordedAsReusedOnSameDestination_StaysCreated_AndNullFingerprintsKeepStored()
    {
        await Run<SquidDbContext, IOctopusImportSourceMapDataProvider>(async (db, provider) =>
        {
            await provider.UpsertAsync([Row("Deployment", "Deployments-1", 30, "Created", "fp-d", "fp-details")], Guid.NewGuid());

            await provider.UpsertAsync([Row("Deployment", "Deployments-1", 30, "Reused")], Guid.NewGuid());

            Describe(await LoadRawRowsAsync(db)).ShouldBe([$"{SpaceId}|Deployment|DEPLOYMENTS-1|30|Created|fp-d|fp-details"]);
        });
    }

    // A history unit whose task was re-created without details no longer holds them: keeping the stored detail
    // fingerprint would make every later REST fetch skip the details for good.
    [Fact]
    public async Task Upsert_DetailsCleared_ClearsStoredDetailFingerprint_KeepsTheRest()
    {
        await Run<SquidDbContext, IOctopusImportSourceMapDataProvider>(async (db, provider) =>
        {
            await provider.UpsertAsync([Row("Deployment", "Deployments-1", 30, "Created", "fp-d", "fp-details"), Row("Deployment", "Deployments-2", 31, "Created", "fp-d2", "fp-details2")], Guid.NewGuid());

            await provider.UpsertAsync([Row("Deployment", " deployments-1 ", 30, "Reused", "fp-new") with { DetailsCleared = true }], Guid.NewGuid());

            Describe(await LoadRawRowsAsync(db)).ShouldBe([$"{SpaceId}|Deployment|DEPLOYMENTS-1|30|Created|fp-new|", $"{SpaceId}|Deployment|DEPLOYMENTS-2|31|Created|fp-d2|fp-details2"], ignoreOrder: true);
        });
    }

    [Fact]
    public async Task Upsert_NewDestination_ReplacesOutcomeAndFingerprints()
    {
        var latestSession = Guid.NewGuid();

        await Run<SquidDbContext, IOctopusImportSourceMapDataProvider>(async (db, provider) =>
        {
            await provider.UpsertAsync([Row("Release", "Releases-1", 20, "Created", "fp-old", "fp-old-details")], Guid.NewGuid());

            await provider.UpsertAsync([Row("Release", "Releases-1", 21, "Reused")], latestSession);

            var row = (await LoadRawRowsAsync(db)).Single();
            Describe([row]).ShouldBe([$"{SpaceId}|Release|RELEASES-1|21|Reused||"]);
            row.LastSessionId.ShouldBe(latestSession);
        });
    }

    [Fact]
    public async Task Upsert_ManyRows_SpansBatches_DuplicateKeysLastWins()
    {
        var rows = Enumerable.Range(1, OctopusImportSourceMapDataProvider.UpsertBatchSize * 2 + 1).Select(i => Row("Variable", $"Variables-{i}", i, "Created")).ToList();
        rows.Add(Row("Variable", "variables-1", 5000, "Reused"));

        await Run<SquidDbContext, IOctopusImportSourceMapDataProvider>(async (db, provider) =>
        {
            await provider.UpsertAsync(rows, Guid.NewGuid());

            var stored = await LoadRawRowsAsync(db);
            stored.Count.ShouldBe(OctopusImportSourceMapDataProvider.UpsertBatchSize * 2 + 1);
            stored.Single(r => r.SourceId == "VARIABLES-1").DestinationId.ShouldBe(5000);
        });
    }

    [Fact]
    public async Task Upsert_SameKeyTwiceInOneCall_LastWins()
    {
        // One INSERT ... ON CONFLICT cannot touch the same row twice, so duplicates must collapse before the statement.
        await Run<SquidDbContext, IOctopusImportSourceMapDataProvider>(async (db, provider) =>
        {
            await provider.UpsertAsync([Row("Release", "Releases-1", 20, "Created"), Row("Release", " releases-1 ", 21, "Created", "fp-last")], Guid.NewGuid());

            Describe(await LoadRawRowsAsync(db)).ShouldBe([$"{SpaceId}|Release|RELEASES-1|21|Created|fp-last|"]);
        });
    }

    [Theory]
    [InlineData("Release", "Releases-1", 0, "Created")]
    [InlineData("Release", "Releases-1", 20, "Updated")]
    [InlineData("Release", " ", 20, "Created")]
    [InlineData("", "Releases-1", 20, "Created")]
    public async Task Upsert_InvalidRow_ThrowsBeforeWriting(string type, string id, int destinationId, string outcome)
    {
        await Run<SquidDbContext, IOctopusImportSourceMapDataProvider>(async (db, provider) =>
        {
            await Should.ThrowAsync<ArgumentException>(() => provider.UpsertAsync([Row("Project", "Projects-1", 10, "Created"), Row(type, id, destinationId, outcome)], Guid.NewGuid()));

            (await LoadRawRowsAsync(db)).ShouldBeEmpty();
        });
    }

    // ── Load ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Load_DropsStaleRows()
    {
        await Run<IRepository, IUnitOfWork, IOctopusImportSourceMapDataProvider>(async (repository, unitOfWork, provider) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            var environment = await builder.CreateEnvironmentAsync(Unique("Env"));
            var project = await builder.CreateProjectAsync(0);
            var channel = await builder.CreateChannelAsync(project.Id);
            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0");
            var orphanRelease = await builder.CreateReleaseAsync(project.Id + 1000, channel.Id, "2.0.0");

            await provider.UpsertAsync([
                Row("Environment", "Environments-1", environment.Id, "Created", "fp-env"),
                Row("Environment", "Environments-2", environment.Id + 1000, "Created"),
                Row("Release", "Releases-1", release.Id, "Created", "fp-rel"),
                Row("Release", "Releases-2", release.Id + 1000, "Created"),
                Row("Release", "Releases-3", orphanRelease.Id, "Created"),
                Row("Certificate", "Certificates-1", environment.Id, "Created"),
                Row("Release", "Releases-9", release.Id, "Created", spaceId: SpaceId + 1)], Guid.NewGuid());

            var prior = await provider.LoadAsync(SpaceId);

            prior.Mappings.Count.ShouldBe(2);
            prior.TryGet("Environment", "environments-1", out var environmentMapping).ShouldBeTrue();
            environmentMapping.ShouldBe(new OctopusImportPriorMapping(environment.Id, "Created", "fp-env", null));
            prior.TryGet("Release", "Releases-1", out var releaseMapping).ShouldBeTrue();
            releaseMapping.DestinationId.ShouldBe(release.Id);
            prior.TryGet("Release", "Releases-2", out _).ShouldBeFalse(customMessage: "a row whose destination was deleted must be dropped as stale");
            prior.TryGet("Release", "Releases-3", out _).ShouldBeFalse(customMessage: "a release whose project was deleted is an orphan, so its row must be dropped as stale");
            prior.TryGet("Certificate", "Certificates-1", out _).ShouldBeFalse(customMessage: "a type with no known table can never be verified, so it is never trusted");
            prior.InstallationId.ShouldBeNull();
        });
    }

    // Squid deletes never cascade: when an owner row is deleted, every row it owned survives as an orphan, and a map
    // row pointing at such an orphan must be stale too, or the re-created owner would be left empty.
    [Theory]
    [InlineData("project", "Project", "Channel,VariableSet:project,Variable:project,DeploymentProcess,DeploymentStep,DeploymentAction,Release,Deployment")]
    [InlineData("library_variable_set", "LibraryVariableSet", "VariableSet:library,Variable:library")]
    [InlineData("lifecycle", "Lifecycle", "LifecyclePhase")]
    [InlineData("channel", "Channel", "Release,Deployment")]
    [InlineData("release", "Release", "Deployment")]
    [InlineData("environment", "Environment", "Deployment")]
    public async Task Load_OwnerDeleted_DropsTheRowsItOwned(string ownerTable, string ownerLabel, string expectedStale)
    {
        await Run<SquidDbContext, IRepository, IUnitOfWork, IOctopusImportSourceMapDataProvider>(async (db, repository, unitOfWork, provider) =>
        {
            var rows = await SeedOwnerGraphAsync(new TestDataBuilder(repository, unitOfWork));
            await provider.UpsertAsync(rows.Values.ToList(), Guid.NewGuid());
            (await provider.LoadAsync(SpaceId)).Mappings.Count.ShouldBe(rows.Count, "every row is live while its owners exist");

            await db.Database.ExecuteSqlRawAsync($"DELETE FROM {ownerTable} WHERE id = {{0}}", rows[ownerLabel].DestinationId);

            var prior = await provider.LoadAsync(SpaceId);
            var stale = rows.Where(pair => pair.Key != ownerLabel && !prior.TryGet(pair.Value.SourceType, pair.Value.SourceId, out _)).Select(pair => pair.Key);
            stale.ShouldBe(expectedStale.Split(','), ignoreOrder: true);
        });
    }

    [Fact]
    public async Task Load_EmptySpace_ReturnsNoMappings()
    {
        await Run<IOctopusImportSourceMapDataProvider>(async provider =>
        {
            var prior = await provider.LoadAsync(SpaceId);

            prior.Mappings.ShouldBeEmpty();
            prior.InstallationId.ShouldBeNull();
        });
    }

    [Fact]
    public async Task Load_InstallationRow_ExposesInstallationId()
    {
        await Run<SquidDbContext, IOctopusImportSourceMapDataProvider>(async (db, provider) =>
        {
            var space = new Space { Name = "Imported space", Slug = "imported-space" };
            db.Set<Space>().Add(space);
            await db.SaveChangesAsync();
            var spaceId = space.Id;

            await provider.UpsertAsync([Row(OctopusImportSourceMapQueries.InstallationSourceType, "a1b2c3d4-0000-4000-8000-000000000001", spaceId, "Created", spaceId: spaceId)], Guid.NewGuid());

            (await provider.LoadAsync(spaceId)).InstallationId.ShouldBe("A1B2C3D4-0000-4000-8000-000000000001");
        });
    }

    // ── Conflict discovery wiring ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Discover_FromTheContainer_LoadsPriorMappings_AndReleaseKeysOfPriorMappedProjectsOnly()
    {
        await Run<IRepository, IUnitOfWork, ILifetimeScope>(async (repository, unitOfWork, scope) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);
            var mapped = await builder.CreateProjectAsync(0);
            var unmapped = await builder.CreateProjectAsync(0);
            var channel = await builder.CreateChannelAsync(mapped.Id, name: "Main");
            var db = scope.Resolve<SquidDbContext>();
            db.Set<Release>().AddRange(
                new Release { Version = "1.0.0", ProjectId = mapped.Id, ChannelId = channel.Id, SpaceId = SpaceId },
                new Release { Version = "2.0.0", ProjectId = unmapped.Id, ChannelId = channel.Id, SpaceId = SpaceId },
                new Release { Version = "3.0.0", ProjectId = mapped.Id, ChannelId = channel.Id, SpaceId = SpaceId + 1 });
            await db.SaveChangesAsync();

            await scope.Resolve<IOctopusImportSourceMapDataProvider>().UpsertAsync([Row("Project", "Projects-1", mapped.Id, "Created"), Row("Channel", "Channels-1", channel.Id, "Created")], Guid.NewGuid());

            var graph = new OctopusResourceGraph([ReleaseNode("Releases-1", "Projects-1"), ReleaseNode("Releases-2", "Projects-2")], [], [], []) { InstallationId = "Installation-A" };

            var result = await scope.Resolve<IOctopusImportConflictDiscoveryService>().DiscoverAsync(SpaceId, graph);

            result.PriorMappings.TryGet("Project", "Projects-1", out var project).ShouldBeTrue(customMessage: "the container must inject the source-map provider into conflict discovery");
            project.DestinationId.ShouldBe(mapped.Id);
            result.ExistingReleaseKeys.ShouldBe([new OctopusImportReleaseKey(mapped.Id, channel.Id, "1.0.0")]);
            result.HistoryInstallationId.ShouldBe("Installation-A");
        });
    }

    // ── Advisory lock ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdvisoryLock_SerializesTwoTransactions()
    {
        var firstLocked = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = Run<SquidDbContext, IOctopusImportSourceMapDataProvider>(async (db, provider) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await provider.AcquireSpaceLockAsync(SpaceId);
            firstLocked.SetResult();
            await releaseFirst.Task;
            await provider.UpsertAsync([Row("Release", "Releases-1", 20, "Created")], Guid.NewGuid());
            await transaction.CommitAsync();
        });

        await firstLocked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = LockAndCountInOwnTransactionAsync(SpaceId);

        await Task.Delay(TimeSpan.FromSeconds(1));
        second.IsCompleted.ShouldBeFalse("the second confirmation must block on the space advisory lock while the first transaction is open");

        releaseFirst.SetResult();
        await first;

        (await second.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBe(1, customMessage: "after the first commit the second caller must see its source-map row — check pg_locks if this times out");
    }

    [Fact]
    public async Task AdvisoryLock_OtherSpace_DoesNotBlock()
    {
        var holderLocked = new TaskCompletionSource();
        var releaseHolder = new TaskCompletionSource();

        var holder = Run<SquidDbContext, IOctopusImportSourceMapDataProvider>(async (db, provider) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await provider.AcquireSpaceLockAsync(SpaceId);
            holderLocked.SetResult();
            await releaseHolder.Task;
        });

        await holderLocked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        try
        {
            (await LockAndCountInOwnTransactionAsync(SpaceId + 1).WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(0);
        }
        finally
        {
            releaseHolder.SetResult();
            await holder;
        }
    }

    [Fact]
    public async Task AdvisoryLock_OutsideATransaction_Refuses()
    {
        await Run<IOctopusImportSourceMapDataProvider>(async provider =>
            await Should.ThrowAsync<InvalidOperationException>(() => provider.AcquireSpaceLockAsync(SpaceId)));
    }

    // ── Release notes ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Release_PersistsReleaseNote()
    {
        await Run<SquidDbContext>(async db =>
        {
            var withNotes = new Release { Version = "1.0.0", ReleaseNote = "Imported from Octopus\nline two", SpaceId = SpaceId };
            var withoutNotes = new Release { Version = "1.0.1", SpaceId = SpaceId };
            db.Set<Release>().AddRange(withNotes, withoutNotes);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            (await db.Set<Release>().AsNoTracking().SingleAsync(r => r.Id == withNotes.Id)).ReleaseNote.ShouldBe("Imported from Octopus\nline two");
            (await db.Set<Release>().AsNoTracking().SingleAsync(r => r.Id == withoutNotes.Id)).ReleaseNote.ShouldBeNull();
            (await db.Database.SqlQueryRaw<string>("SELECT release_notes AS \"Value\" FROM release WHERE id = {0}", withNotes.Id).SingleAsync()).ShouldBe("Imported from Octopus\nline two");
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private Task<int> LockAndCountInOwnTransactionAsync(int spaceId)
        => Run<ILifetimeScope, int>(async lifetime =>
        {
            var db = lifetime.Resolve<SquidDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await lifetime.Resolve<IOctopusImportSourceMapDataProvider>().AcquireSpaceLockAsync(spaceId);
            return await db.Set<OctopusImportSourceMap>().CountAsync();
        });

    private static OctopusResourceNode ReleaseNode(string id, string projectId)
        => new(id, "1.0.0", OctopusResourceKind.Release, OctopusDocumentKind.Release, $"{id}.json", projectId, null, false, new OctopusReleaseDto { Id = id, ProjectId = projectId, Version = "1.0.0" });

    // Every owned kind with its owners, keyed by a test label.
    private static async Task<Dictionary<string, OctopusImportSourceMapRow>> SeedOwnerGraphAsync(TestDataBuilder builder)
    {
        var projectSet = await builder.CreateVariableSetAsync();
        var librarySet = await builder.CreateVariableSetAsync(Squid.Message.Enums.VariableSetOwnerType.LibraryVariableSet);
        var library = await builder.CreateLibraryVariableSetAsync(librarySet.Id, Unique("Library"));
        var process = await builder.CreateDeploymentProcessAsync();
        var project = await builder.CreateProjectAsync(projectSet.Id, process.Id);
        var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Step");
        var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Action");
        var lifecycle = await builder.CreateLifecycleAsync(Unique("Lifecycle"));
        var phase = await builder.CreateEmptyLifecyclePhaseAsync(lifecycle.Id);
        var environment = await builder.CreateEnvironmentAsync(Unique("Env"));
        var channel = await builder.CreateChannelAsync(project.Id);
        var release = await builder.CreateReleaseAsync(project.Id, channel.Id);
        var deployment = await builder.CreateDeploymentAsync(project.Id, environment.Id, release.Id, 0, channel.Id);

        return new Dictionary<string, OctopusImportSourceMapRow>
        {
            ["Project"] = Row("Project", "Projects-1", project.Id, "Created"),
            ["LibraryVariableSet"] = Row("LibraryVariableSet", "LibraryVariableSets-1", library.Id, "Created"),
            ["Lifecycle"] = Row("Lifecycle", "Lifecycles-1", lifecycle.Id, "Created"),
            ["Environment"] = Row("Environment", "Environments-1", environment.Id, "Created"),
            ["Channel"] = Row("Channel", "Channels-1", channel.Id, "Created"),
            ["VariableSet:project"] = Row("VariableSet", "variableset-Projects-1", projectSet.Id, "Created"),
            ["Variable:project"] = Row("Variable", "Variables-1", (await builder.CreateVariableAsync(projectSet.Id, "Project", "1")).Id, "Created"),
            ["VariableSet:library"] = Row("VariableSet", "variableset-LibraryVariableSets-1", librarySet.Id, "Created"),
            ["Variable:library"] = Row("Variable", "Variables-2", (await builder.CreateVariableAsync(librarySet.Id, "Library", "2")).Id, "Created"),
            ["DeploymentProcess"] = Row("DeploymentProcess", "deploymentprocess-Projects-1", process.Id, "Created"),
            ["DeploymentStep"] = Row("DeploymentStep", "Steps-1", step.Id, "Created"),
            ["DeploymentAction"] = Row("DeploymentAction", "Actions-1", action.Id, "Created"),
            ["LifecyclePhase"] = Row("LifecyclePhase", "Phases-1", phase.Id, "Created"),
            ["Release"] = Row("Release", "Releases-1", release.Id, "Created"),
            ["Deployment"] = Row("Deployment", "Deployments-1", deployment.Id, "Created")
        };
    }

    private static OctopusImportSourceMapRow Row(string type, string id, int destinationId, string outcome, string fingerprint = null, string detailFingerprint = null, int spaceId = SpaceId)
        => new(spaceId, type, id, destinationId, outcome, fingerprint, detailFingerprint);

    private static OctopusImportIdMappingDto Mapping(string sourceId, string sourceType, int destinationId, string outcome, string sourceName)
        => new() { SourceId = sourceId, SourceType = sourceType, SourceName = sourceName, DestinationType = sourceType, DestinationId = destinationId, OutcomeState = Enum.Parse<OctopusImportResourceOutcomeState>(outcome) };

    private static async Task InsertSessionAsync(SquidDbContext db, int spaceId, string state, DateTimeOffset completedAt, params OctopusImportIdMappingDto[] mappings)
    {
        var resultJson = JsonSerializer.Serialize(new OctopusImportSessionResultDto { IdMappings = mappings.ToList() }, ResultJsonOptions);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO octopus_import_session (session_id, destination_space_id, owner_user_id, state, result_json, data_version, expires_at, completed_at, last_state_changed_at)
            VALUES ({Guid.NewGuid()}, {spaceId}, 1, {state}, CAST({resultJson} AS jsonb), {Guid.NewGuid().ToByteArray()}, {completedAt.AddDays(1)}, {completedAt}, {completedAt})
            """);
    }

    // Re-runs the real migration file: proves the backfill and that the whole script is safely re-runnable.
    private static async Task RunMigrationAsync(SquidDbContext db)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, DbUpRunner.ScriptFolder, MigrationFile));
        var connection = db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static Task<List<OctopusImportSourceMap>> LoadRawRowsAsync(SquidDbContext db)
        => db.Set<OctopusImportSourceMap>().AsNoTracking().ToListAsync();

    private static List<string> Describe(IEnumerable<OctopusImportSourceMap> rows)
        => rows.Select(r => $"{r.SpaceId}|{r.SourceType}|{r.SourceId}|{r.DestinationId}|{r.OutcomeState}|{r.Fingerprint}|{r.DetailFingerprint}").Order().ToList();

    private static string Unique(string value) => $"{value} {Guid.NewGuid():N}";
}
