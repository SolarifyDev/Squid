using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

public sealed class OctopusRestHistoryFetcherTests : IDisposable
{
    private readonly string _sessionDirectory = Path.Combine(Path.GetTempPath(), $"squid-history-fetch-{Guid.NewGuid():N}");
    private readonly OctopusImportHistoryDetailsStore _detailsStore = new();

    [Fact]
    public async Task ChunkResumesFromCursor()
    {
        var server = new FakeOctopusServer();
        foreach (var n in Enumerable.Range(1, 150)) server.Deployment("Projects-2", n);
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1", "Projects-2"], ProjectIndex = 1, Phase = OctopusHistoryFetchPhase.Deployments, Skip = 100, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };

        var progress = await Fetcher(server).FetchChunkAsync(Context(cursor));

        server.Paths.ShouldContain("/api/Spaces-1/deployments?projects=Projects-2&skip=100&take=100");
        server.Paths.ShouldNotContain(p => p.Contains("Projects-1"));
        server.Paths.ShouldNotContain(p => p.Contains("skip=0&take=100") && p.Contains("deployments"));
        progress.Counts.Deployments.ShouldBe(50);
        cursor.ProjectIndex.ShouldBe(2);
        cursor.Done.ShouldBeTrue();
    }

    // The time budget is checked per deployment, and the cursor records every deployment written, so a slow page
    // still makes progress chunk by chunk instead of being restarted from its first item every time.
    [Fact]
    public async Task ChunkBudgetSpent_StopsAfterTheCurrentItem_AndTheNextChunkResumesAtTheNextOne()
    {
        var server = new FakeOctopusServer();
        foreach (var n in Enumerable.Range(1, 150)) server.Deployment("Projects-1", n);
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, Skip = 100, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = Fetcher(server, oneItemPerChunk: true);

        await fetcher.FetchChunkAsync(Context(cursor));

        (cursor.Phase, cursor.Skip, cursor.Counts.Deployments, cursor.Counts.ServerTasks, cursor.Counts.DetailsFetched).ShouldBe((OctopusHistoryFetchPhase.Deployments, 101, 1, 1, 1));

        await fetcher.FetchChunkAsync(Context(cursor));

        (cursor.Skip, cursor.Counts.Deployments).ShouldBe((102, 2));
        server.Paths.ShouldContain("/api/Spaces-1/deployments?projects=Projects-1&skip=101&take=100");
        server.Paths.Count(p => p.Contains("/details")).ShouldBe(2);
    }

    [Fact]
    public async Task PagingWithinAProject_AdvancesSkipUntilTheLastPage()
    {
        var server = new FakeOctopusServer();
        foreach (var n in Enumerable.Range(1, 150)) server.Release("Projects-1", n);
        var cursor = new OctopusImportHistoryFetchDto();

        await Fetcher(server).FetchChunkAsync(Context(cursor, exportProjectIds: ["Projects-1"]));

        cursor.Counts.Releases.ShouldBe(150);
        server.Paths.ShouldContain("/api/Spaces-1/projects/Projects-1/releases?skip=0&take=100");
        server.Paths.ShouldContain("/api/Spaces-1/projects/Projects-1/releases?skip=100&take=100");
    }

    // A unit that fails part way is neither counted nor skipped: the retry fetches it again and counts it once.
    [Theory]
    [InlineData("/details")]
    [InlineData("/events")]
    [InlineData("/machines/all")]
    public async Task FailedUnit_RetriedOnTheSameCursor_CountedOnce(string failingPath)
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        var failures = 0;
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith(failingPath, StringComparison.Ordinal) && failures++ == 0 ? OctopusRestStubHandler.Status(System.Net.HttpStatusCode.BadRequest) : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = Fetcher(server);

        await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)));

        (cursor.Skip, cursor.Counts.Deployments, cursor.Counts.ServerTasks, cursor.Counts.DetailsFetched).ShouldBe((0, 0, 0, 0));

        await fetcher.FetchChunkAsync(Context(cursor));

        cursor.Done.ShouldBeTrue();
        (cursor.Counts.Deployments, cursor.Counts.ServerTasks, cursor.Counts.DetailsFetched).ShouldBe((1, 1, 1));
    }

    [Fact]
    public async Task TaskDetails_AskOnlyForTheLogTailTheImporterKeeps()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);

        await Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), exportProjectIds: ["Projects-1"]));

        server.Paths.ShouldContain($"/api/Spaces-1/tasks/ServerTasks-1/details?verbose=false&tail={OctopusImportDeploymentHistoryImporter.MaxLogLinesPerTask()}");
    }

    // One task whose log is over the per-response cap must not fail the whole history: it is imported without details.
    [Fact]
    public async Task TaskDetailsOverTheResponseCap_TaskKeptWithoutDetails_FetchContinues()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1).Deployment("Projects-1", 2);
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith("ServerTasks-1/details", StringComparison.Ordinal) ? DeclaredTooLarge() : null;

        var progress = await Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), exportProjectIds: ["Projects-1"]));

        progress.Done.ShouldBeTrue();
        (progress.Counts.Deployments, progress.Counts.ServerTasks, progress.Counts.DetailsFetched, progress.Counts.DetailsTooLarge).ShouldBe((2, 2, 1, 1));
        (await _detailsStore.ReadAsync(_sessionDirectory, "ServerTasks-1")).ShouldBeNull();
        (await _detailsStore.ReadAsync(_sessionDirectory, "ServerTasks-2")).ShouldNotBeNull();
        server.Paths.ShouldNotContain(p => p.Contains("regarding=ServerTasks-1"));
    }

    // A project the chosen Octopus space does not have (a wrong space, or deleted in Octopus) is reported, not silently empty.
    [Fact]
    public async Task ProjectMissingInTheSpace_ReportedInProgress()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1);
        server.Override = request => request.RequestUri!.AbsolutePath.Contains("/projects/Projects-9/", StringComparison.Ordinal) ? OctopusRestStubHandler.Status(System.Net.HttpStatusCode.NotFound) : null;

        var progress = await Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), exportProjectIds: ["Projects-1", "Projects-9"]));

        progress.Done.ShouldBeTrue();
        progress.Counts.ProjectsNotFound.ShouldBe(1);
        progress.Counts.Releases.ShouldBe(1);
    }

    [Fact]
    public async Task DetailFetchOutlivingTheChunkDeadline_Unreachable_ItemNotCommitted()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) ? OctopusRestStubHandler.StalledBody() : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero, ChunkOverrun = TimeSpan.FromMilliseconds(300) });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)).WaitAsync(TimeSpan.FromSeconds(30)));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Unreachable);
        (cursor.Skip, cursor.Counts.Deployments).ShouldBe((0, 0));
    }

    // The history byte budget also bounds a single detail list, not just the total checked between items.
    [Fact]
    public async Task EndlessDetailList_StoppedByTheHistoryByteBudget()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) ? EndlessEventsPage(request) : null;
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { HistoryMaxBytes = 50_000 });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), exportProjectIds: ["Projects-1"])));

        ex.Kind.ShouldBe(OctopusRestErrorKind.TooLarge);
        server.Paths.Count(p => p.Contains("/events")).ShouldBeLessThan(50);
    }

    [Fact]
    public async Task OneChunkPerPage_AdvancesThroughReleasesDeploymentsAndProjects()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1).Deployment("Projects-1", 1);
        var cursor = new OctopusImportHistoryFetchDto { OctopusSpaceId = "Spaces-1" };
        var fetcher = Fetcher(server, oneItemPerChunk: true);
        var phases = new List<(OctopusHistoryFetchPhase, int)>();

        while (!cursor.Done && phases.Count < 10)
        {
            await fetcher.FetchChunkAsync(Context(cursor, exportProjectIds: ["Projects-1"]));
            phases.Add((cursor.Phase, cursor.ProjectIndex));
        }

        phases.ShouldBe([(OctopusHistoryFetchPhase.Deployments, 0), (OctopusHistoryFetchPhase.Releases, 1), (OctopusHistoryFetchPhase.Done, 1)]);
        File.Exists(Path.Combine(_sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName)).ShouldBeTrue();
    }

    [Fact]
    public async Task IncrementalSkip_NoDetailCalls()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1).Deployment("Projects-1", 2);
        var unchangedTask = JsonSerializer.Deserialize<OctopusServerTaskDto>(JsonSerializer.Serialize(server.Tasks["ServerTasks-1"]));
        var prior = Prior(("Project", "Projects-1", null, null), ("Deployment", "Deployments-1", OctopusImportFingerprint.ForTask(unchangedTask), "stored-details"), ("ServerTask", "ServerTasks-1", null, null));
        var cursor = new OctopusImportHistoryFetchDto();

        var progress = await Fetcher(server).FetchChunkAsync(Context(cursor, prior));

        server.Paths.ShouldNotContain(p => p.Contains("ServerTasks-1/details") || p.Contains("regarding=ServerTasks-1") || p.Contains("regarding=Deployments-1"));
        server.Paths.ShouldContain(p => p.Contains("ServerTasks-2/details"));
        progress.Counts.DetailsSkipped.ShouldBe(1);
        progress.Counts.DetailsFetched.ShouldBe(1);
        (await _detailsStore.ReadAsync(_sessionDirectory, "ServerTasks-1")).ShouldBeNull();
        (await _detailsStore.ReadAsync(_sessionDirectory, "ServerTasks-2")).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(true, true, true)]    // task changed in Octopus since the import → details re-fetched
    [InlineData(false, false, true)]  // task unchanged but the import stored no details (file import, #542 backfill) → fetched
    [InlineData(false, true, false)]  // task unchanged and details stored, but the imported task row is gone → fetched for the re-created task
    public async Task ChangedTaskOrMissingStoredDetails_FetchesDetails(bool taskChanged, bool detailsStored, bool taskRowLive)
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1, taskChanged ? "Failed" : "Success");
        var storedTask = JsonSerializer.Deserialize<OctopusServerTaskDto>(JsonSerializer.Serialize(server.Tasks["ServerTasks-1"]));
        storedTask.State = taskChanged ? "Executing" : storedTask.State;
        var taskRow = taskRowLive ? ("ServerTask", "ServerTasks-1", (string)null, (string)null) : ("Environment", "Environments-1", null, null);
        var prior = Prior(("Project", "Projects-1", null, null), ("Deployment", "Deployments-1", OctopusImportFingerprint.ForTask(storedTask), detailsStored ? "stored-details" : null), taskRow);

        var progress = await Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), prior));

        progress.Counts.DetailsFetched.ShouldBe(1);
        progress.Counts.DetailsSkipped.ShouldBe(0);
        server.Paths.ShouldContain(p => p.Contains("ServerTasks-1/details"));
    }

    [Fact]
    public async Task RePage_OverwritesNotDuplicates()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1).Release("Projects-1", 2).Deployment("Projects-1", 1);
        var fetcher = Fetcher(server, oneItemPerChunk: true);
        var cursor = new OctopusImportHistoryFetchDto();

        await fetcher.FetchChunkAsync(Context(cursor, exportProjectIds: ["Projects-1"]));
        cursor.Phase = OctopusHistoryFetchPhase.Releases;
        cursor.Skip = 0;

        for (var chunk = 0; !cursor.Done && chunk < 10; chunk++) await fetcher.FetchChunkAsync(Context(cursor));

        var manifest = ReadBundleManifest();
        manifest.Entries.Select(e => e.Id).ShouldBe(["Releases-1", "Releases-2", "Deployments-1", "ServerTasks-1"], ignoreOrder: true);
        Directory.EnumerateFiles(Path.Combine(_sessionDirectory, OctopusHistoryBundleWriter.StagingDirectoryName), "*.json", SearchOption.AllDirectories).Count().ShouldBe(4);
    }

    [Fact]
    public async Task SpacelessServer()
    {
        var server = new FakeOctopusServer { Spaceless = true }.Release("Projects-1", 1).Deployment("Projects-1", 1);
        var cursor = new OctopusImportHistoryFetchDto();

        await Fetcher(server).FetchChunkAsync(Context(cursor, exportProjectIds: ["Projects-1"]));

        cursor.Done.ShouldBeTrue();
        cursor.Spaceless.ShouldBeTrue();
        server.Paths.ShouldContain("/api/projects/Projects-1/releases?skip=0&take=100");
        server.Paths.ShouldContain("/api/deployments?projects=Projects-1&skip=0&take=100");
        server.Paths.ShouldContain("/api/tasks?ids=ServerTasks-1&skip=0&take=1");
        server.Paths.ShouldNotContain(p => p.Contains("spaces", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SpaceNotGiven_UsesTheDefaultSpace()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1);
        var cursor = new OctopusImportHistoryFetchDto();

        await Fetcher(server).FetchChunkAsync(Context(cursor, exportProjectIds: ["Projects-1"]));

        cursor.OctopusSpaceId.ShouldBe("Spaces-1");
        server.Paths.ShouldContain("/api/Spaces-1/projects/Projects-1/releases?skip=0&take=100");
        server.Paths.ShouldContain(p => p.StartsWith("/api/tasks?") || p.StartsWith("/api/Spaces-1/deployments"));
    }

    [Fact]
    public async Task EmptyScope_Error()
    {
        var server = new FakeOctopusServer();
        var otherInstallation = Prior("inst-other", ("Project", "Projects-1", null, null));

        await Should.ThrowAsync<OctopusImportHistoryScopeEmptyException>(() => Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), otherInstallation)));

        server.Paths.ShouldNotContain(p => p.Contains("projects/") || p.Contains("deployments"));
    }

    [Theory]
    [InlineData(new[] { "Projects-2" }, null, new[] { "Projects-1", "Projects-2" })]
    [InlineData(new string[0], "inst-1", new[] { "Projects-1" })]
    [InlineData(new[] { "Projects-1" }, null, new[] { "Projects-1" })]
    public void ScopeProjectIds_ExportPlusPriorFromTheSameInstallation(string[] exportIds, string priorInstallation, string[] expected)
    {
        var context = new OctopusHistoryFetchContext { ExportProjectIds = exportIds, PriorMappings = Prior(priorInstallation, ("Project", "Projects-1", null, null), ("Release", "Releases-1", null, null)) };

        OctopusRestHistoryFetcher.ScopeProjectIds(context, "inst-1").ShouldBe(expected);
    }

    // Export project ids carry the export's 32-hex suffix; REST paths need the bare id, and the prior row is the same project.
    [Fact]
    public void ScopeProjectIds_ExportSuffixedIds_AreFetchedByTheirRestId()
    {
        var context = new OctopusHistoryFetchContext { ExportProjectIds = ["Projects-2-0123456789ABCDEF0123456789ABCDEF", "Projects-1-0123456789abcdef0123456789abcdef"], PriorMappings = Prior("inst-1", ("Project", "Projects-1", null, null)) };

        OctopusRestHistoryFetcher.ScopeProjectIds(context, "inst-1").ShouldBe(["Projects-1", "Projects-2"]);
    }

    [Fact]
    public async Task InstallationChangedBehindTheSameUrl_Rejected()
    {
        var server = new FakeOctopusServer { InstallationId = "inst-2" }.Release("Projects-1", 1);
        var cursor = new OctopusImportHistoryFetchDto { InstallationId = "inst-1", ProjectIds = ["Projects-1"] };

        var ex = await Should.ThrowAsync<OctopusImportHistoryServerMismatchException>(() => Fetcher(server).FetchChunkAsync(Context(cursor)));

        ex.PinnedField.ShouldBe("installation");
        server.Paths.ShouldBe(["/api"]);
    }

    [Fact]
    public async Task Done_BuildsABundleThePlannerCanRead_WithDetailsFeedsAndMachineNames()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1).Deployment("Projects-1", 1);

        await Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), exportProjectIds: ["Projects-1"]));

        var bundlePath = Path.Combine(_sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName);
        var extraction = await new OctopusImportSourceReader(new OctopusArchiveExtractor(), new OctopusInputExtractor()).ReadAsync(bundlePath);
        var inventory = new OctopusManifestInventoryBuilder().Build(extraction);
        var graph = new OctopusResourceGraphBuilder().Build(inventory);

        inventory.Diagnostics.ShouldNotContain(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
        graph.InstallationId.ShouldBe("inst-1");
        var task = graph.Resources.Single(r => r.SourceId == "ServerTasks-1").GetSource<OctopusServerTaskDto>();
        var details = await _detailsStore.ReadAsync(_sessionDirectory, "ServerTasks-1");
        task.DetailLevel.ShouldBe(1);
        task.DetailFingerprint.ShouldBe(OctopusImportFingerprint.ForTaskDetails(details));
        task.ProjectId.ShouldBe("Projects-1");
        details.Interruptions.ShouldHaveSingleItem().Id.ShouldBe("Interruptions-1");
        details.Events.ShouldHaveSingleItem().Category.ShouldBe("DeploymentSucceeded");
        details.Artifacts.ShouldHaveSingleItem().Filename.ShouldBe("report.txt");
        var release = graph.Resources.Single(r => r.SourceId == "Releases-1").GetSource<OctopusReleaseDto>();
        release.SelectedPackages.Single().ExtensionData[OctopusRestHistoryFetcher.FeedIdProperty].GetString().ShouldBe("Feeds-7");
        var deployment = graph.Resources.Single(r => r.SourceId == "Deployments-1").GetSource<OctopusDeploymentDto>();
        deployment.ExtensionData[OctopusRestHistoryFetcher.DeployedToMachineNamesProperty].Deserialize<string[]>().ShouldBe(["web-01", "Machines-9"]);
    }

    [Fact]
    public async Task HistoryOverTheByteCap_TooLarge()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1);
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { HistoryMaxBytes = 64 });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), exportProjectIds: ["Projects-1"])));

        ex.Kind.ShouldBe(OctopusRestErrorKind.TooLarge);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sessionDirectory)) Directory.Delete(_sessionDirectory, recursive: true);
    }

    private OctopusRestHistoryFetcher Fetcher(FakeOctopusServer server, bool oneItemPerChunk = false)
        => new(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { ChunkDuration = oneItemPerChunk ? TimeSpan.Zero : TimeSpan.FromSeconds(5) });

    private static System.Net.Http.HttpResponseMessage DeclaredTooLarge()
    {
        var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("{}") };
        response.Content.Headers.ContentLength = new OctopusRestOptions().MaxResponseBytes + 1;
        return response;
    }

    private static System.Net.Http.HttpResponseMessage EndlessEventsPage(System.Net.Http.HttpRequestMessage request)
    {
        var skip = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["skip"]!);

        return OctopusRestStubHandler.Json(OctopusRestStubHandler.Page(Enumerable.Range(skip, 100).Select(i => new { Id = $"Events-{i}", Category = "DeploymentSucceeded" }).ToList(), int.MaxValue));
    }

    private OctopusHistoryFetchContext Context(OctopusImportHistoryFetchDto cursor, OctopusImportPriorMappings prior = null, IReadOnlyList<string> exportProjectIds = null)
        => new() { ServerUrl = FakeOctopusServer.Url, ApiKey = FakeOctopusServer.ApiKey, SessionDirectory = _sessionDirectory, Cursor = cursor, PriorMappings = prior ?? OctopusImportPriorMappings.Empty, ExportProjectIds = exportProjectIds ?? [] };

    private static OctopusImportPriorMappings Prior(params (string Type, string Id, string Fingerprint, string DetailFingerprint)[] rows) => Prior("inst-1", rows);

    private static OctopusImportPriorMappings Prior(string installationId, params (string Type, string Id, string Fingerprint, string DetailFingerprint)[] rows)
        => new(rows.ToDictionary(r => (r.Type, r.Id), r => new OctopusImportPriorMapping(1, "Created", r.Fingerprint, r.DetailFingerprint)), installationId);

    private OctopusExportManifestDto ReadBundleManifest()
    {
        using var zip = ZipFile.OpenRead(Path.Combine(_sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName));
        using var manifest = zip.GetEntry("manifest.json")!.Open();

        return JsonSerializer.Deserialize<OctopusExportManifestDto>(manifest);
    }
}
