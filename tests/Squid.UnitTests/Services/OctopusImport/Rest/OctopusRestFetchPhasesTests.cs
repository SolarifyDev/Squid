using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// The whole-space server fetch: Catalog → per project ProjectConfiguration → Releases → Deployments → Closure → Done,
/// driven chunk by chunk against the 2026.1-shaped fixtures (plus synthetic history), and read back through the real
/// source reader, inventory and graph builders.
/// </summary>
public sealed class OctopusRestFetchPhasesTests : IDisposable
{
    private const int SpaceId = 7;

    private static readonly OctopusResourceKind[] HistoryKinds = [OctopusResourceKind.Release, OctopusResourceKind.Deployment, OctopusResourceKind.ServerTask, OctopusResourceKind.DeploymentProcessSnapshot];

    private readonly List<string> _directories = [];

    // ── Whole space ────────────────────────────────────────────────────────

    [Fact]
    public async Task WholeFixtureSpace_EveryResourceStagedOnce_SystemTeamOnlyWhenReferenced_BuiltInFeedsNever()
    {
        var server = FixtureSpace();
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(server, directory, cursor);

        cursor.ProjectIds.ShouldBe(["Projects-1", "Projects-2"]);
        var bundled = BundleManifest(directory).Entries.Select(e => e.Id).ToList();
        bundled.ShouldBe(ExpectedConfigurationIds().Concat(["Releases-1", "Deployments-1", "ServerTasks-1"]), ignoreOrder: true);
        bundled.ShouldNotContain("teams-administrators", "a system team nothing references is not imported");
        server.Paths.ShouldContain("/api/teams?spaces=Spaces-1&includeSystem=false&skip=0&take=100");
        server.Paths.ShouldContain("/api/teams?ids=teams-everyone&skip=0&take=1&includeSystem=true", "the manual intervention's system team is fetched by the closure");
        server.Paths.ShouldNotContain(p => p.Contains("environments?ids") || p.Contains("feeds?ids") || p.Contains("channels?ids"), "references already staged are never fetched again");
    }

    [Fact]
    public async Task WholeFixtureSpace_CountsEveryConfigurationDocumentAndWithheldSecretOnce()
    {
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(FixtureSpace(), directory, cursor);

        cursor.Counts.ConfigurationDocuments.ShouldBe(ExpectedConfigurationIds().Count);
        cursor.Counts.SecretsWithheld.ShouldBe(ExpectedWithheldSecrets());
        cursor.Counts.SecretsWithheld.ShouldBeGreaterThan(0);
        (cursor.Counts.Releases, cursor.Counts.Deployments, cursor.Counts.ServerTasks).ShouldBe((1, 1, 1));
    }

    // Producer → consumer: the bundle the fetch writes reads back clean and plans with only the blockers later work owns.
    [Fact]
    public async Task WholeFixtureSpace_BundleReadsBackCleanAndPlans()
    {
        var directory = NewSessionDirectory();

        await FetchUntilDoneAsync(FixtureSpace(), directory, CatalogCursor());

        var (inventory, graph) = await ReadBundleAsync(directory);
        inventory.Diagnostics.ShouldBeEmpty();
        graph.Diagnostics.ShouldNotContain(d => d.Code == OctopusInputExtractionDiagnosticCodes.GraphDocumentMalformed);
        graph.InstallationId.ShouldBe("inst-1");
        (await PlanBlockersAsync(graph)).ShouldBe([OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing]);
    }

    // ── Phase order ────────────────────────────────────────────────────────

    [Fact]
    public async Task OneItemPerChunk_RunsCatalogThenEachProjectsConfigurationReleasesDeploymentsThenClosure()
    {
        var server = FixtureSpace();
        var cursor = CatalogCursor();
        var fetcher = Fetcher(server, NewSessionDirectory(), oneItemPerChunk: true);
        var before = new List<(OctopusHistoryFetchPhase Phase, int Project, int Catalog)>();

        List<string> referencesAtClosure = null;

        for (var chunk = 0; !cursor.Done && chunk < 200; chunk++)
        {
            before.Add((cursor.Phase, cursor.ProjectIndex, cursor.CatalogIndex));
            if (cursor.Phase == OctopusHistoryFetchPhase.Closure) referencesAtClosure ??= [.. cursor.ClosureReferences];
            await fetcher.Fetcher.FetchChunkAsync(fetcher.Context(cursor));
        }

        cursor.Done.ShouldBeTrue();
        Compress(before.Select(b => (b.Phase, b.Project))).ShouldBe(
        [
            (OctopusHistoryFetchPhase.Catalog, 0),
            (OctopusHistoryFetchPhase.ProjectConfiguration, 0), (OctopusHistoryFetchPhase.Releases, 0), (OctopusHistoryFetchPhase.Deployments, 0),
            (OctopusHistoryFetchPhase.ProjectConfiguration, 1), (OctopusHistoryFetchPhase.Releases, 1), (OctopusHistoryFetchPhase.Deployments, 1),
            (OctopusHistoryFetchPhase.Closure, 2)
        ], "the last project's deployments lead straight into the closure");
        Compress(before.Where(b => b.Phase == OctopusHistoryFetchPhase.Catalog).Select(b => b.Catalog)).ShouldBe(Enumerable.Range(0, OctopusRestHistoryFetcher.CatalogCollections.Count));
        referencesAtClosure.ShouldBe([OctopusRestHistoryFetcher.ClosureKey(OctopusResourceKind.Team, "teams-everyone")], "the persisted cursor only carries references the session has not staged");
    }

    [Fact]
    public async Task SpaceWithoutProjects_StillBundlesItsCatalog()
    {
        var server = new FakeOctopusServer();
        server.Resources["environments"] = new JsonArray(Resource("Environments-1", "Production"));
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(server, directory, cursor);

        cursor.ProjectIds.ShouldBeEmpty();
        BundleManifest(directory).Entries.Select(e => e.Id).ShouldBe(["Environments-1"]);
        server.Paths.ShouldNotContain(p => p.Contains("/projects/"));
    }

    [Fact]
    public async Task SpacelessServer_ReadsUnprefixedCollectionsAndAllTeams()
    {
        var server = new FakeOctopusServer { Spaceless = true };
        server.Resources["environments"] = new JsonArray(Resource("Environments-1", "Production"));

        await FetchUntilDoneAsync(server, NewSessionDirectory(), CatalogCursor());

        server.Paths.ShouldContain("/api/environments?skip=0&take=100");
        server.Paths.ShouldContain("/api/teams?includeSystem=false&skip=0&take=100");
    }

    [Fact]
    public void CatalogOrder_Pinned_ProjectsLast()
    {
        // A persisted cursor holds an index into this list: reordering it would resume an in-flight fetch at the wrong kind.
        OctopusRestHistoryFetcher.CatalogCollections.Select(c => (c.Kind, c.Name, c.IsRoot)).ShouldBe(
        [
            (OctopusResourceKind.ProjectGroup, "projectgroups", false), (OctopusResourceKind.Environment, "environments", false), (OctopusResourceKind.Lifecycle, "lifecycles", false),
            (OctopusResourceKind.Feed, "feeds", false), (OctopusResourceKind.Account, "accounts", false), (OctopusResourceKind.Team, "teams", true),
            (OctopusResourceKind.LibraryVariableSet, "libraryvariablesets", false), (OctopusResourceKind.ActionTemplate, "actiontemplates", false), (OctopusResourceKind.Machine, "machines", false),
            (OctopusResourceKind.Certificate, "certificates", false), (OctopusResourceKind.WorkerPool, "workerpools", false), (OctopusResourceKind.Tenant, "tenants", false),
            (OctopusResourceKind.Project, "projects", false)
        ]);
        OctopusRestHistoryFetcher.ClosureCollections.Select(c => c.Kind).ShouldBe([OctopusResourceKind.Team, OctopusResourceKind.Environment, OctopusResourceKind.Feed, OctopusResourceKind.Channel]);
    }

    [Fact]
    public async Task Progress_ReportsTheCatalogKind()
    {
        var server = FixtureSpace();
        var cursor = CatalogCursor();
        var fetcher = Fetcher(server, NewSessionDirectory(), oneItemPerChunk: true);

        // Two project groups: the first chunk stages one, the second the other and moves on to the next kind.
        var first = await fetcher.Fetcher.FetchChunkAsync(fetcher.Context(cursor));
        var second = await fetcher.Fetcher.FetchChunkAsync(fetcher.Context(cursor));

        (first.Phase, first.CatalogIndex, first.CatalogKindCount).ShouldBe((OctopusHistoryFetchPhase.Catalog, 0, OctopusRestHistoryFetcher.CatalogCollections.Count));
        (second.Phase, second.CatalogIndex).ShouldBe((OctopusHistoryFetchPhase.Catalog, 1));
    }

    // ── Paging drift ───────────────────────────────────────────────────────

    // Ten environments are deleted in Octopus after the first page: the rest shift forward, so paging on from skip=100 would
    // never see the ten that moved onto the first page (101-110). The smaller total restarts the kind; none is lost.
    [Fact]
    public async Task CatalogKindShrinksBetweenPages_RestartsTheKind_NoResourceLost()
    {
        var server = new FakeOctopusServer();
        var environments = new JsonArray(Enumerable.Range(1, 150).Select(n => (JsonNode)Resource($"Environments-{n}", $"env-{n}")).ToArray());
        server.Resources["environments"] = environments;
        var deleted = false;
        server.Override = request =>
        {
            if (!deleted && request.RequestUri!.PathAndQuery.Contains("/environments?skip=100", StringComparison.Ordinal))
            {
                deleted = true;
                for (var i = 0; i < 10; i++) environments.RemoveAt(0);
            }

            return null;
        };
        var directory = NewSessionDirectory();

        await FetchUntilDoneAsync(server, directory, CatalogCursor());

        BundleManifest(directory).Entries.Select(e => e.Id).ShouldBe(Enumerable.Range(1, 150).Select(n => $"Environments-{n}"), ignoreOrder: true, "the ten deleted ones were staged before they went; 101-110 only by the restart");
    }

    // ── Staged machine names ───────────────────────────────────────────────

    [Fact]
    public async Task DeploymentMachineNames_ComeFromTheStagedMachineDocuments_NeverAnotherRequest()
    {
        var server = FixtureSpace();
        var directory = NewSessionDirectory();

        await FetchUntilDoneAsync(server, directory, CatalogCursor());

        var (_, graph) = await ReadBundleAsync(directory);
        var deployment = graph.Resources.Single(r => r.SourceId == "Deployments-1").GetSource<OctopusDeploymentDto>();
        deployment.ExtensionData[OctopusRestHistoryFetcher.DeployedToMachineNamesProperty].Deserialize<string[]>().ShouldBe(["dev-cluster", "Machines-9"], "Machines-9 is not in the space, so it keeps its id");
        server.Paths.Count(p => p.Contains("/machines")).ShouldBe(1, "machines are read once, by the catalog");
    }

    // ── Closure ────────────────────────────────────────────────────────────

    // A deployment names an environment and a channel created in Octopus after the catalog and the project's configuration
    // were read: the closure fetches exactly those by id, and the bundle holds them.
    [Fact]
    public async Task ResourcesCreatedDuringTheFetch_FetchedByIdInTheClosure()
    {
        var server = FixtureSpace();
        server.Deployments["Projects-1"][0]["EnvironmentId"] = "Environments-9";
        server.Deployments["Projects-1"][0]["ChannelId"] = "Channels-9";
        server.Override = request => request.RequestUri!.PathAndQuery switch
        {
            var p when p.Contains("/environments?ids=Environments-9", StringComparison.Ordinal) => OctopusRestStubHandler.Json(OctopusRestStubHandler.Page([Resource("Environments-9", "created later")], 1)),
            var p when p.Contains("/channels?ids=Channels-9", StringComparison.Ordinal) => OctopusRestStubHandler.Json(OctopusRestStubHandler.Page([Resource("Channels-9", "created later", ("ProjectId", "Projects-1"))], 1)),
            _ => null
        };
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(server, directory, cursor);

        BundleManifest(directory).Entries.Select(e => e.Id).ShouldContain("Environments-9");
        BundleManifest(directory).Entries.Select(e => e.Id).ShouldContain("Channels-9");
        cursor.ClosureReferences.ShouldBeEmpty();
    }

    // An action scoped to an environment, or taking a package from a feed, created after the catalog read its kind: the
    // process is the only document naming it (the release snapshot's feed is another one), so only the process can bring it in.
    [Theory]
    [InlineData("Environments", "environments", "Environments-9")]
    [InlineData("ExcludedEnvironments", "environments", "Environments-9")]
    [InlineData("Packages", "feeds", "Feeds-9")]
    public async Task ProcessActionReferenceCreatedDuringTheFetch_FetchedByIdInTheClosure(string actionMember, string collection, string id)
    {
        var server = FixtureSpace();
        var action = server.Resources["projects/Projects-1/deploymentprocesses"]!["Steps"]!.AsArray().SelectMany(s => s!["Actions"]!.AsArray()).OfType<JsonObject>().Single(a => a["Packages"]!.AsArray().Count > 0);
        if (actionMember == "Packages") action["Packages"]![0]!["FeedId"] = id;
        else action[actionMember] = new JsonArray(id);
        server.Override = request => request.RequestUri!.PathAndQuery.Contains($"/{collection}?ids={id}", StringComparison.Ordinal)
            ? OctopusRestStubHandler.Json(OctopusRestStubHandler.Page([Resource(id, "created later", ("FeedType", "NuGet"), ("FeedUri", "https://nuget.example.com/"))], 1))
            : null;
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(server, directory, cursor);

        BundleManifest(directory).Entries.Select(e => e.Id).ShouldContain(id);
        cursor.ClosureReferences.ShouldBeEmpty();
    }

    // A reference recorded before a later project staged it (a deployment naming another project's channel) is not asked for
    // again: the closure has nothing left to fetch for it.
    [Fact]
    public async Task ReferenceStagedLaterInTheFetch_NotRequestedAgainByTheClosure()
    {
        var server = FixtureSpace();
        server.Deployments["Projects-1"][0]["ChannelId"] = "Channels-2";
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(server, directory, cursor);

        BundleManifest(directory).Entries.Select(e => e.Id).ShouldContain("Channels-2");
        server.Paths.ShouldNotContain(p => p.Contains("/channels?ids=", StringComparison.Ordinal));
        cursor.ClosureReferences.ShouldBeEmpty();
    }

    // Every closure batch is an item of its own: one whose request keeps outliving the deadline is skipped after
    // MaxAbandonments, counted from zero for each batch (a batch abandoned once and then fetched does not lend its count to
    // the next), and the fetch still finishes.
    [Fact]
    public async Task ClosureBatchAbandonedTooOften_SkippedForTheSpace_OtherBatchesFetched_FetchFinishes()
    {
        var server = FixtureSpace();
        var environment8Requests = 0;
        server.Override = request => request.RequestUri!.PathAndQuery switch
        {
            var p when p.Contains("/environments?ids=Environments-8", StringComparison.Ordinal) => ++environment8Requests == 1 ? OctopusRestStubHandler.StalledBody() : OctopusRestStubHandler.Json(OctopusRestStubHandler.Page([Resource("Environments-8", "created later")], 1)),
            var p when p.Contains("/environments?ids=Environments-9", StringComparison.Ordinal) => OctopusRestStubHandler.StalledBody(),
            _ => null
        };
        var directory = NewSessionDirectory();
        var cursor = new OctopusImportHistoryFetchDto { Phase = OctopusHistoryFetchPhase.Closure, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1", ClosureReferences = [OctopusRestHistoryFetcher.ClosureKey(OctopusResourceKind.Environment, "Environments-8"), OctopusRestHistoryFetcher.ClosureKey(OctopusResourceKind.Environment, "Environments-9")] };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), new OctopusImportHistoryDetailsStore(), new OctopusRestOptions { ChunkDuration = TimeSpan.Zero, ChunkOverrun = TimeSpan.FromMilliseconds(300), PageSize = 1 });
        var failures = 0;

        for (var chunk = 0; !cursor.Done && chunk < 20; chunk++)
        {
            try { await fetcher.FetchChunkAsync(Context(cursor, directory)).WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (OctopusRestException e) when (e.Kind == OctopusRestErrorKind.Unreachable) { failures++; }
        }

        cursor.Done.ShouldBeTrue();
        failures.ShouldBe(1 + OctopusRestHistoryFetcher.MaxAbandonments - 1, "Environments-8 abandoned once, Environments-9 abandoned until skipped");
        (cursor.Counts.ItemsAbandoned, cursor.ClosureReferences.Count).ShouldBe((1, 0));
        var skipped = cursor.SkippedItems.ShouldHaveSingleItem();
        (skipped.ProjectId, skipped.Reason).ShouldBe(((string)null, OctopusHistoryFetchSkipReason.Abandoned));
        skipped.Path.ShouldContain("Environments-9");
        var bundled = BundleManifest(directory).Entries.Select(e => e.Id).ToList();
        bundled.ShouldContain("Environments-8");
        bundled.ShouldNotContain("Environments-9");
    }

    // A server that ignores the ids filter returns more than was asked for; only the referenced resources are staged.
    [Fact]
    public async Task ClosureAnswerWithUnrequestedResources_OnlyTheReferencedOnesStaged()
    {
        var server = FixtureSpace();
        server.Override = request => request.RequestUri!.PathAndQuery.Contains("/teams?ids=", StringComparison.Ordinal)
            ? OctopusRestStubHandler.Json(OctopusRestStubHandler.Page([Resource("teams-everyone", "Everyone"), Resource("teams-administrators", "Administrators")], 2))
            : null;
        var directory = NewSessionDirectory();

        await FetchUntilDoneAsync(server, directory, CatalogCursor());

        var bundled = BundleManifest(directory).Entries.Select(e => e.Id).ToList();
        bundled.ShouldContain("teams-everyone");
        bundled.ShouldNotContain("teams-administrators");
    }

    // ── Project configuration ──────────────────────────────────────────────

    // An older server has no deployment settings endpoint; its project document carries the settings instead.
    [Fact]
    public async Task DeploymentSettingsEndpointMissing_SettingsTakenFromTheProjectDocument()
    {
        var server = FixtureSpace();
        server.Resources.Remove("projects/Projects-1/deploymentsettings");
        var directory = NewSessionDirectory();

        await FetchUntilDoneAsync(server, directory, CatalogCursor());

        var (_, graph) = await ReadBundleAsync(directory);
        var settings = graph.Resources.Single(r => r.SourceId == "deploymentsettings-Projects-1").GetSource<OctopusDeploymentSettingsDto>();
        settings.ProjectId.ShouldBe("Projects-1");
        settings.ConnectivityPolicy.ShouldNotBeNull();
    }

    [Fact]
    public async Task DeploymentSettingsRefused_RecordedForTheProject_NotGuessedFromTheProject()
    {
        var server = FixtureSpace();
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/projects/Projects-1/deploymentsettings", StringComparison.Ordinal) ? OctopusRestStubHandler.Status(HttpStatusCode.Forbidden) : null;
        var directory = NewSessionDirectory();
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(server, directory, cursor);

        BundleManifest(directory).Entries.Select(e => e.Id).ShouldNotContain("deploymentsettings-Projects-1");
        var skipped = cursor.SkippedItems.ShouldHaveSingleItem();
        (skipped.ProjectId, skipped.Reason, skipped.Path).ShouldBe(("Projects-1", OctopusHistoryFetchSkipReason.Rejected, "api/Spaces-1/projects/Projects-1/deploymentsettings"));
    }

    // A space resource refused inside a catalog item is recorded against no project.
    [Fact]
    public async Task CatalogItemPartlyRefused_RecordedForTheSpace()
    {
        var server = FixtureSpace();
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/variables/variableset-LibraryVariableSets-2", StringComparison.Ordinal) ? OctopusRestStubHandler.Status(HttpStatusCode.Forbidden) : null;
        var cursor = CatalogCursor();

        await FetchUntilDoneAsync(server, NewSessionDirectory(), cursor);

        var skipped = cursor.SkippedItems.ShouldHaveSingleItem();
        (skipped.ProjectId, skipped.Path).ShouldBe((null, "api/Spaces-1/variables/variableset-LibraryVariableSets-2"));
        cursor.Counts.ItemsRejected.ShouldBe(1);
    }

    // The projects kind is read after some project ids are known: a refused page of it is still the space's, not a project's.
    [Fact]
    public async Task RefusedCatalogPage_AfterProjectsWereListed_RecordedForTheSpace()
    {
        var server = new FakeOctopusServer { ClientOptions = new OctopusRestOptions { PageSize = 1 } };
        server.Resources["projects"] = new JsonArray(Resource("Projects-1", "one"), Resource("Projects-2", "two"));
        server.Override = request => request.RequestUri!.PathAndQuery.Contains("/projects?skip=1", StringComparison.Ordinal) ? OctopusRestStubHandler.Status(HttpStatusCode.Forbidden) : null;
        var cursor = CatalogCursor();
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), new OctopusImportHistoryDetailsStore(), new OctopusRestOptions { PageSize = 1, ChunkDuration = TimeSpan.FromSeconds(5) });

        await fetcher.FetchChunkAsync(Context(cursor, NewSessionDirectory()));

        var skipped = cursor.SkippedItems.ShouldHaveSingleItem();
        (skipped.ProjectId, skipped.Path).ShouldBe((null, "api/Spaces-1/projects?skip=1&take=1"));
        cursor.ProjectIds.ShouldBe(["Projects-1"]);
    }

    // A project configuration that keeps outliving the deadline is skipped as a whole: nothing of it is staged and the
    // fetch moves on to the project's releases.
    [Fact]
    public async Task ProjectConfigurationAbandonedTooOften_SkippedAsAWhole_FetchMovesToItsReleases()
    {
        var server = FixtureSpace();
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/projects/Projects-1/variables", StringComparison.Ordinal) ? OctopusRestStubHandler.StalledBody() : null;
        var directory = NewSessionDirectory();
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.ProjectConfiguration, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), new OctopusImportHistoryDetailsStore(), new OctopusRestOptions { ChunkDuration = TimeSpan.Zero, ChunkOverrun = TimeSpan.FromMilliseconds(300) });

        for (var attempt = 1; attempt < OctopusRestHistoryFetcher.MaxAbandonments; attempt++)
            (await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor, directory)).WaitAsync(TimeSpan.FromSeconds(30)))).Kind.ShouldBe(OctopusRestErrorKind.Unreachable);

        await fetcher.FetchChunkAsync(Context(cursor, directory)).WaitAsync(TimeSpan.FromSeconds(30));

        (cursor.Phase, cursor.Skip, cursor.Counts.ItemsAbandoned, cursor.Counts.ConfigurationDocuments).ShouldBe((OctopusHistoryFetchPhase.Releases, 0, 1, 0));
        var skipped = cursor.SkippedItems.ShouldHaveSingleItem();
        (skipped.ProjectId, skipped.Reason).ShouldBe(("Projects-1", OctopusHistoryFetchSkipReason.Abandoned));
        Directory.Exists(Path.Combine(directory, OctopusHistoryBundleWriter.StagingDirectoryName, nameof(OctopusResourceKind.DeploymentProcess))).ShouldBeFalse("every read of the configuration comes before its first write");
    }

    // ── Resume (H5) ────────────────────────────────────────────────────────

    // A chunk that fails at the first request of every phase and is called again produces the same bundle, byte for byte,
    // and the same counts as a fetch that never failed.
    [Fact]
    public async Task FailureAtEveryPhaseBoundary_ResumedFetch_ProducesTheSameBundleAndCounts()
    {
        var cleanDirectory = NewSessionDirectory();
        var clean = CatalogCursor();
        await FetchUntilDoneAsync(FixtureSpace(), cleanDirectory, clean);

        var server = FixtureSpace();
        var failOnce = new HashSet<string>(["/projectgroups", "/projects/Projects-1/deploymentprocesses", "/projects/Projects-1/releases", "/deployments", "/projects/Projects-2/deploymentprocesses", "/api/teams?ids"], StringComparer.Ordinal);
        server.Override = request => failOnce.FirstOrDefault(f => Uri.UnescapeDataString(request.RequestUri!.PathAndQuery).Contains(f, StringComparison.Ordinal)) is { } failing && failOnce.Remove(failing) ? OctopusRestStubHandler.Status(HttpStatusCode.MethodNotAllowed) : null;
        var resumedDirectory = NewSessionDirectory();
        var resumed = CatalogCursor();
        var fetcher = Fetcher(server, resumedDirectory, oneItemPerChunk: true);
        var failures = 0;

        for (var chunk = 0; !resumed.Done && chunk < 300; chunk++)
        {
            try { await fetcher.Fetcher.FetchChunkAsync(fetcher.Context(resumed)); }
            catch (OctopusRestException) { failures++; }
        }

        failures.ShouldBe(6);
        failOnce.ShouldBeEmpty();
        BundleHash(resumedDirectory).ShouldBe(BundleHash(cleanDirectory));
        JsonSerializer.Serialize(resumed.Counts).ShouldBe(JsonSerializer.Serialize(clean.Counts));
    }

    public void Dispose()
    {
        foreach (var directory in _directories.Where(Directory.Exists)) Directory.Delete(directory, recursive: true);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // The release's package comes from a feed the space has, so the bundle plans without a missing feed.
    private static FakeOctopusServer FixtureSpace() => new FakeOctopusServer { SnapshotFeedId = "Feeds-1" }.WithFixtureSpace().Release("Projects-1", 1).Deployment("Projects-1", 1);

    private static OctopusImportHistoryFetchDto CatalogCursor() => new() { Phase = OctopusHistoryFetchPhase.Catalog };

    private string NewSessionDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"squid-fetch-phases-{Guid.NewGuid():N}");
        _directories.Add(directory);
        return directory;
    }

    private static (OctopusRestHistoryFetcher Fetcher, Func<OctopusImportHistoryFetchDto, OctopusHistoryFetchContext> Context) Fetcher(FakeOctopusServer server, string directory, bool oneItemPerChunk = false)
        => (new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), new OctopusImportHistoryDetailsStore(), new OctopusRestOptions { ChunkDuration = oneItemPerChunk ? TimeSpan.Zero : TimeSpan.FromSeconds(5) }), cursor => Context(cursor, directory));

    private static OctopusHistoryFetchContext Context(OctopusImportHistoryFetchDto cursor, string directory)
        => new() { ServerUrl = FakeOctopusServer.Url, ApiKey = FakeOctopusServer.ApiKey, SessionDirectory = directory, Cursor = cursor, PriorMappings = OctopusImportPriorMappings.Empty };

    private static async Task FetchUntilDoneAsync(FakeOctopusServer server, string directory, OctopusImportHistoryFetchDto cursor)
    {
        var fetcher = Fetcher(server, directory);

        for (var chunk = 0; !cursor.Done && chunk < 50; chunk++)
            await fetcher.Fetcher.FetchChunkAsync(fetcher.Context(cursor));

        cursor.Done.ShouldBeTrue();
    }

    private static JsonObject Resource(string id, string name, params (string Member, string Value)[] members)
    {
        var resource = new JsonObject { ["Id"] = id, ["Name"] = name, ["SpaceId"] = "Spaces-1" };
        foreach (var (member, value) in members) resource[member] = value;
        return resource;
    }

    // Every configuration document the fixture space holds, as the normaliser shapes it; system teams only when referenced.
    private static List<string> ExpectedConfigurationIds()
        => ConfigurationDocuments().Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static int ExpectedWithheldSecrets() => ConfigurationDocuments().Sum(d => d.WithheldSecrets.Count);

    private static IEnumerable<OctopusRestNormalizedDocument> ConfigurationDocuments()
        => OctopusRestFixtureKinds.All.Where(f => !HistoryKinds.Contains(f.Kind))
            .SelectMany(f => OctopusServer2026Fixtures.Resources(f.Fixture).Select(r => OctopusRestDocumentNormalizer.Normalize(f.Kind, r, OctopusRestFixtureKinds.OwnerOf(f.Fixture))))
            .Where(d => d != null && (d.Kind != OctopusResourceKind.Team || !d.Id.StartsWith("teams-", StringComparison.Ordinal) || d.Id == "teams-everyone"));

    private static List<T> Compress<T>(IEnumerable<T> values)
    {
        var compressed = new List<T>();
        foreach (var value in values.Where(v => compressed.Count == 0 || !EqualityComparer<T>.Default.Equals(compressed[^1], v))) compressed.Add(value);
        return compressed;
    }

    private static string BundlePath(string directory) => Path.Combine(directory, OctopusImportSourceReader.HistoryBundleFileName);

    private static string BundleHash(string directory) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BundlePath(directory))));

    private static OctopusExportManifestDto BundleManifest(string directory)
    {
        using var zip = ZipFile.OpenRead(BundlePath(directory));
        using var manifest = zip.GetEntry("manifest.json")!.Open();

        return JsonSerializer.Deserialize<OctopusExportManifestDto>(manifest);
    }

    private static async Task<(OctopusManifestInventoryResult Inventory, OctopusResourceGraph Graph)> ReadBundleAsync(string directory)
    {
        var extraction = await new OctopusImportSourceReader(new OctopusArchiveExtractor(), new OctopusInputExtractor(), new OctopusImportSizeLimits()).ReadAsync(BundlePath(directory));
        var inventory = new OctopusManifestInventoryBuilder().Build(extraction);

        return (inventory, new OctopusResourceGraphBuilder().Build(inventory));
    }

    private static async Task<List<string>> PlanBlockersAsync(OctopusResourceGraph graph)
    {
        var dependencyPlan = new OctopusImportDependencyPlanner().BuildCurrentConfigurationPlan(graph);
        var conflicts = await new OctopusImportConflictDiscoveryService(EmptyDestination(), EmptySourceMap()).DiscoverAsync(SpaceId, graph);
        var accessor = new OctopusExportSecretsAccessor { Secrets = OctopusExportSecrets.None };
        var preview = new OctopusImportPreviewPlanner(accessor).BuildPreviewPlan(dependencyPlan, conflicts);
        var validation = new OctopusImportPreviewValidator(accessor).Validate(graph, dependencyPlan, conflicts, preview);

        return preview.Diagnostics.Concat(preview.Resources.SelectMany(r => r.Diagnostics)).Concat(validation.Diagnostics).Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => d.Code).Distinct().Order().ToList();
    }

    private static IOctopusImportDestinationDataProvider EmptyDestination()
    {
        var destination = new Mock<IOctopusImportDestinationDataProvider>();
        destination.Setup(d => d.GetResourcesAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync([SquidBuiltInTeam(1, BuiltInTeamNames.Everyone), SquidBuiltInTeam(2, BuiltInTeamNames.Administrators)]);
        destination.Setup(d => d.GetReleaseKeysAsync(SpaceId, It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return destination.Object;
    }

    private static OctopusImportDestinationResource SquidBuiltInTeam(int id, string name) => new(id, 0, OctopusResourceKind.Team, name, null, DateTimeOffset.UtcNow.AddDays(-1)) { IsBuiltIn = true, MemberCount = 0 };

    private static IOctopusImportSourceMapDataProvider EmptySourceMap()
    {
        var sourceMap = new Mock<IOctopusImportSourceMapDataProvider>();
        sourceMap.Setup(s => s.LoadAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(OctopusImportPriorMappings.Empty);
        return sourceMap.Object;
    }
}
