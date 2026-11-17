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

        await Fetcher(server).FetchChunkAsync(Context(cursor, projects: ["Projects-1"]));

        cursor.Counts.Releases.ShouldBe(150);
        server.Paths.ShouldContain("/api/Spaces-1/projects/Projects-1/releases?skip=0&take=100");
        server.Paths.ShouldContain("/api/Spaces-1/projects/Projects-1/releases?skip=100&take=100");
    }

    // A unit that fails part way is neither counted nor skipped: the retry fetches it again and counts it once.
    // 405 is a failure the client neither retries nor tolerates as one refused item (that is 400/403/409/422).
    [Theory]
    [InlineData("/details")]
    [InlineData("/events")]
    [InlineData("/artifacts")]
    public async Task FailedUnit_RetriedOnTheSameCursor_CountedOnce(string failingPath)
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        var failures = 0;
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith(failingPath, StringComparison.Ordinal) && failures++ == 0 ? OctopusRestStubHandler.Status(System.Net.HttpStatusCode.MethodNotAllowed) : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = Fetcher(server);

        await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)));

        (cursor.Skip, cursor.Counts.Deployments, cursor.Counts.ServerTasks, cursor.Counts.DetailsFetched).ShouldBe((0, 0, 0, 0));

        await fetcher.FetchChunkAsync(Context(cursor));

        cursor.Done.ShouldBeTrue();
        (cursor.Counts.Deployments, cursor.Counts.ServerTasks, cursor.Counts.DetailsFetched).ShouldBe((1, 1, 1));
    }

    // D14: a request Octopus refuses (400/403/409/422) skips that one item, is counted once and listed with its
    // project; the deployment it belongs to is still written and the fetch finishes.
    [Theory]
    [InlineData("/details", System.Net.HttpStatusCode.BadRequest)]
    [InlineData("/events", System.Net.HttpStatusCode.Forbidden)]
    [InlineData("/interruptions", System.Net.HttpStatusCode.Conflict)]
    [InlineData("/artifacts", System.Net.HttpStatusCode.UnprocessableEntity)]
    [InlineData("/api/tasks", System.Net.HttpStatusCode.Forbidden)]
    [InlineData("/deploymentprocesses/deploymentprocess-Projects-1-s-1", System.Net.HttpStatusCode.Forbidden)]
    public async Task RejectedRequestInsideAnItem_ItemSkippedCountedOnce_FetchCompletes(string failingPath, System.Net.HttpStatusCode status)
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1).Deployment("Projects-1", 1);
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith(failingPath, StringComparison.Ordinal) ? OctopusRestStubHandler.Status(status) : null;
        var cursor = new OctopusImportHistoryFetchDto();

        var progress = await Fetcher(server).FetchChunkAsync(Context(cursor, projects: ["Projects-1"]));

        progress.Done.ShouldBeTrue();
        (progress.Counts.Releases, progress.Counts.Deployments, progress.Counts.ItemsRejected).ShouldBe((1, 1, 1));
        var skipped = cursor.SkippedItems.ShouldHaveSingleItem();
        (skipped.ProjectId, skipped.Reason).ShouldBe(("Projects-1", OctopusHistoryFetchSkipReason.Rejected));
        ("/" + skipped.Path).ShouldContain(failingPath);
    }

    // A refused page (all releases, or all deployments, of a project) is skipped as a whole and the project still finishes.
    [Theory]
    [InlineData("/releases")]
    [InlineData("/deployments")]
    public async Task RejectedPage_SkippedAndRecorded_NotReportedAsAMissingProject(string failingPath)
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1).Deployment("Projects-1", 1);
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith(failingPath, StringComparison.Ordinal) ? OctopusRestStubHandler.Status(System.Net.HttpStatusCode.Forbidden) : null;
        var cursor = new OctopusImportHistoryFetchDto();

        var progress = await Fetcher(server).FetchChunkAsync(Context(cursor, projects: ["Projects-1"]));

        progress.Done.ShouldBeTrue();
        (progress.Counts.ItemsRejected, progress.Counts.ProjectsNotFound).ShouldBe((1, 0));
        cursor.SkippedItems.ShouldHaveSingleItem().Path.ShouldContain("skip=0&take=100");
    }

    // A list whose first pages were read and whose next page is refused (a WAF answering 403 under load, the key's scope narrowed)
    // is not cut short as if that page were its last: the chunk fails with the cursor on the refused page, nothing is recorded as
    // skipped, and once the refusal clears the next chunk reads the rest of the list.
    [Theory]
    [InlineData(OctopusHistoryFetchPhase.Releases, "/releases")]
    [InlineData(OctopusHistoryFetchPhase.Deployments, "/deployments")]
    public async Task RefusedLaterPage_ChunkFailsOnThatPage_RestOfTheListReadOnceTheRefusalClears(OctopusHistoryFetchPhase phase, string listPath)
    {
        var server = new FakeOctopusServer();
        foreach (var n in Enumerable.Range(1, 150)) server.Release("Projects-1", n).Deployment("Projects-1", n);
        var refusing = true;
        server.Override = request => refusing && request.RequestUri!.AbsolutePath.EndsWith(listPath, StringComparison.Ordinal) && request.RequestUri.Query.Contains("skip=100", StringComparison.Ordinal) ? OctopusRestStubHandler.Status(System.Net.HttpStatusCode.Forbidden) : null;
        var cursor = new OctopusImportHistoryFetchDto { Phase = phase };
        var fetcher = Fetcher(server);

        var ex = await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor, projects: ["Projects-1"])));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Unreachable);
        (cursor.Phase, cursor.Skip, cursor.Done, cursor.Counts.ItemsRejected).ShouldBe((phase, 100, false, 0));
        cursor.SkippedItems.ShouldBeEmpty();

        refusing = false;
        await fetcher.FetchChunkAsync(Context(cursor));

        cursor.Done.ShouldBeTrue();
        (phase == OctopusHistoryFetchPhase.Releases ? cursor.Counts.Releases : cursor.Counts.Deployments).ShouldBe(150);
        cursor.Counts.ItemsRejected.ShouldBe(0);
    }

    // A later page refused because the key's grants were narrowed after the fetch began fails the fetch as a restricted key,
    // which the user must fix, instead of a retry that would only be refused again.
    [Fact]
    public async Task RefusedLaterPage_KeyNarrowedMidFetch_FailsAsRestrictedKey()
    {
        var server = new FakeOctopusServer();
        foreach (var n in Enumerable.Range(1, 150)) server.Release("Projects-1", n);
        var narrowed = false;
        server.Override = request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/releases", StringComparison.Ordinal) && request.RequestUri.Query.Contains("skip=100", StringComparison.Ordinal))
            {
                narrowed = true;
                return OctopusRestStubHandler.Status(System.Net.HttpStatusCode.Forbidden);
            }

            return narrowed && path.EndsWith("/permissions", StringComparison.Ordinal) ? OctopusRestStubHandler.Json(OctopusServer2026Fixtures.Node("users-permissions-restricted")) : null;
        };
        var cursor = new OctopusImportHistoryFetchDto();

        await Should.ThrowAsync<OctopusImportApiKeyRestrictedException>(() => Fetcher(server).FetchChunkAsync(Context(cursor, projects: ["Projects-1"])));

        (cursor.Phase, cursor.Skip, cursor.Counts.Releases, cursor.Counts.ItemsRejected).ShouldBe((OctopusHistoryFetchPhase.Releases, 100, 100, 0));
    }

    // Only-fill: a task whose details Octopus partly refused gets no details and no detail fingerprint, so neither the
    // importer nor the next fetch takes the partial set as complete. Once the refusal clears, the next fetch (seeing
    // what the importer stored: the task, no detail fingerprint) fetches the whole set again.
    [Theory]
    [InlineData("/details")]
    [InlineData("/interruptions")]
    [InlineData("/events")]
    [InlineData("/artifacts")]
    public async Task RejectedDetailRequest_TaskKeptWithoutAnyDetails_FetchedInFullOnceTheRefusalClears(string refusedPath)
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        var refusing = true;
        server.Override = request => refusing && request.RequestUri!.AbsolutePath.EndsWith(refusedPath, StringComparison.Ordinal) ? OctopusRestStubHandler.Status(System.Net.HttpStatusCode.Forbidden) : null;

        var refused = await Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), projects: ["Projects-1"]));

        (refused.Counts.ServerTasks, refused.Counts.DetailsFetched, refused.Counts.ItemsRejected).ShouldBe((1, 0, 1));
        (await _detailsStore.ReadAsync(_sessionDirectory, "ServerTasks-1")).ShouldBeNull("a partial set must never be stored as the task's details");
        var task = await BundledTaskAsync("ServerTasks-1");
        (task.DetailLevel, task.DetailFingerprint, task.DetailsIncomplete).ShouldBe((0, null, true));

        refusing = false;
        var prior = Prior(("Project", "Projects-1", null, null), ("Deployment", "Deployments-1", OctopusImportFingerprint.ForTask(task), null), ("ServerTask", "ServerTasks-1", null, null));
        var refilled = await Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), prior));

        (refilled.Counts.DetailsFetched, refilled.Counts.DetailsSkipped, refilled.Counts.ItemsRejected).ShouldBe((1, 0, 0));
        var details = await _detailsStore.ReadAsync(_sessionDirectory, "ServerTasks-1");
        (details.Interruptions.Count, details.Events.Count, details.Artifacts.Count).ShouldBe((1, 1, 1));
        var complete = await BundledTaskAsync("ServerTasks-1");
        (complete.DetailFingerprint, complete.DetailsIncomplete).ShouldBe((OctopusImportFingerprint.ForTaskDetails(details), false));
    }

    // A refusal recorded inside an item that then fails is not committed: the retry records it once.
    [Fact]
    public async Task RejectedRequestInAnItemThatFails_CountedOnceAfterTheRetry()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        var eventFailures = 0;
        server.Override = request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/interruptions", StringComparison.Ordinal) => OctopusRestStubHandler.Status(System.Net.HttpStatusCode.Forbidden),
            var p when p.EndsWith("/events", StringComparison.Ordinal) && eventFailures++ == 0 => OctopusRestStubHandler.Status(System.Net.HttpStatusCode.MethodNotAllowed),
            _ => null
        };
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = Fetcher(server);

        await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)));

        (cursor.Counts.ItemsRejected, cursor.SkippedItems.Count).ShouldBe((0, 0));

        await fetcher.FetchChunkAsync(Context(cursor));

        (cursor.Counts.ItemsRejected, cursor.SkippedItems.Count, cursor.Counts.Deployments).ShouldBe((1, 1, 1));
    }

    [Fact]
    public async Task SkippedItems_ListCapped_CountsNot()
    {
        var server = new FakeOctopusServer();
        foreach (var n in Enumerable.Range(1, 3)) server.Deployment("Projects-1", n);
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) ? OctopusRestStubHandler.Status(System.Net.HttpStatusCode.Forbidden) : null;
        var cursor = new OctopusImportHistoryFetchDto { SkippedItems = Enumerable.Range(0, OctopusRestHistoryFetcher.MaxRecordedSkippedItems - 1).Select(_ => new OctopusHistoryFetchSkippedItemDto()).ToList() };

        await Fetcher(server).FetchChunkAsync(Context(cursor, projects: ["Projects-1"]));

        cursor.Counts.ItemsRejected.ShouldBe(3);
        cursor.SkippedItems.Count.ShouldBe(OctopusRestHistoryFetcher.MaxRecordedSkippedItems);
    }

    // ── Key binding (D12) ─────────────────────────────────────────────────

    [Fact]
    public async Task FirstChunk_BindsTheKeysUserAndFingerprint_NeverTheKey()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1);
        var cursor = new OctopusImportHistoryFetchDto();

        await Fetcher(server).FetchChunkAsync(Context(cursor, projects: ["Projects-1"]));

        cursor.UserId.ShouldBe(FakeOctopusServer.UserId);
        cursor.KeyHmac.ShouldBe(OctopusRestKeyBinding.Fingerprint(FakeOctopusServer.MasterKey, FakeOctopusServer.ApiKey));
        JsonSerializer.Serialize(cursor).ShouldNotContain(FakeOctopusServer.ApiKey);
    }

    [Fact]
    public async Task LaterChunk_KeyOfAnotherOctopusUser_RefusedBeforeAnyHistoryIsRead()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1);
        server.KeyUsers["API-TEST-OTHER-USER-1"] = "Users-8";
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], InstallationId = "inst-1", OctopusSpaceId = "Spaces-1", UserId = FakeOctopusServer.UserId, KeyHmac = "bound" };

        var ex = await Should.ThrowAsync<OctopusImportHistoryServerMismatchException>(() => Fetcher(server).FetchChunkAsync(Context(cursor, apiKey: "API-TEST-OTHER-USER-1")));

        ex.PinnedField.ShouldBe(OctopusRestKeyBinding.UserPinnedField);
        server.Paths.ShouldBe(["/api", "/api/users/me"]);
        (cursor.UserId, cursor.KeyHmac).ShouldBe((FakeOctopusServer.UserId, "bound"));
    }

    [Fact]
    public async Task LaterChunk_AnotherKeyOfTheSameUser_ContinuesAndRebinds()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1);
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], InstallationId = "inst-1", OctopusSpaceId = "Spaces-1", UserId = FakeOctopusServer.UserId, KeyHmac = "old-key" };

        var progress = await Fetcher(server).FetchChunkAsync(Context(cursor, apiKey: "API-TEST-ROTATED-1"));

        progress.Done.ShouldBeTrue();
        cursor.KeyHmac.ShouldBe(OctopusRestKeyBinding.Fingerprint(FakeOctopusServer.MasterKey, "API-TEST-ROTATED-1"));
    }

    [Fact]
    public async Task UsersMeRefused_Unauthorized()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1);
        server.Override = request => request.RequestUri!.AbsolutePath == "/api/users/me" ? OctopusRestStubHandler.Status(System.Net.HttpStatusCode.Forbidden) : null;
        var cursor = new OctopusImportHistoryFetchDto();

        var ex = await Should.ThrowAsync<OctopusRestException>(() => Fetcher(server).FetchChunkAsync(Context(cursor, projects: ["Projects-1"])));

        ex.Kind.ShouldBe(OctopusRestErrorKind.Unauthorized);
        cursor.UserId.ShouldBeNull();
    }

    // ── Bundling ──────────────────────────────────────────────────────────

    // Building the bundle of a large fetch (hundreds of thousands of documents) can outlast the chunk deadline that bounds an item, and a
    // proxy may drop the browser's request meanwhile. The chunk still holds the session's lease, so bundling runs on to the lease, not to
    // the item deadline or the request: the fetch completes instead of failing as "Unreachable" and restarting the bundle every resume.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundlingOutlivesTheChunkDeadlineOrTheRequest_WithinTheLease_FetchCompletes(bool requestDropped)
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        var writer = new SlowBundleWriter(TimeSpan.FromMilliseconds(900));
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, writer, _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero, ChunkOverrun = TimeSpan.FromMilliseconds(300) });
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Closure, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1", LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        using var request = new CancellationTokenSource();

        writer.Started = () => { if (requestDropped) request.CancelAfter(100); };

        await fetcher.FetchChunkAsync(Context(cursor), request.Token).WaitAsync(TimeSpan.FromSeconds(30));

        (cursor.Done, cursor.Phase).ShouldBe((true, OctopusHistoryFetchPhase.Done));
        File.Exists(Path.Combine(_sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName)).ShouldBeTrue();
    }

    // Bundling that would outlive the lease stops before it, so no second chunk can bundle into the same directory at the same time; the
    // failure names the setting that buys it more time, and the fetch is not done.
    [Fact]
    public async Task BundlingBeyondTheLease_Stopped_TooLargeNamesTheChunkSetting_NotDone()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        var writer = new SlowBundleWriter(TimeSpan.FromSeconds(30));
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, writer, _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero, ChunkOverrun = TimeSpan.FromMilliseconds(300) });
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Closure, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1", LeaseExpiresAt = DateTimeOffset.UtcNow + OctopusRestHistoryFetcher.BundleLeaseSafety + TimeSpan.FromMilliseconds(500) };

        var ex = await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)).WaitAsync(TimeSpan.FromSeconds(20)));

        (ex.Kind, ex.LimitName).ShouldBe((OctopusRestErrorKind.TooLarge, OctopusRestOptions.ChunkSecondsEnvVar));
        cursor.Done.ShouldBeFalse();
    }

    // The session's disk filling up is this Squid server's problem, not the Octopus server's: the failure has its own kind and a fixed
    // message that tells the operator what to free, never the absolute staging path, and never an inner exception the API would echo.
    [Fact]
    public async Task DiskFullWhileStaging_StorageFailure_FixedMessageWithoutPath()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1);
        var fullDisk = new IOException($"No space left on device : '{Path.Combine(_sessionDirectory, "history-staging", "Release", "abc.json")}'");
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new FailingBundleWriter(fullDisk), _detailsStore, new OctopusRestOptions());

        var ex = await Should.ThrowAsync<OctopusImportStorageException>(() => fetcher.FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), projects: ["Projects-1"])));

        ex.Message.ShouldNotContain(_sessionDirectory);
        ex.Message.ShouldContain("OctopusImport:TemporaryUploads:RootPath");
        ex.InnerException.ShouldBeNull();
        OctopusImportErrorKinds.Of(ex).ShouldBe(OctopusImportErrorKinds.Storage);
    }

    private sealed class FailingBundleWriter(IOException failure) : IOctopusHistoryBundleWriter
    {
        private readonly OctopusHistoryBundleWriter _inner = new();

        public Task<long> WriteDocumentAsync(string sessionDirectory, OctopusResourceKind kind, OctopusDocumentDto document, CancellationToken ct = default) => throw failure;

        public Task<long> WriteJsonAsync(string sessionDirectory, OctopusResourceKind kind, System.Text.Json.Nodes.JsonObject document, CancellationToken ct = default) => throw failure;

        public bool IsStaged(string sessionDirectory, OctopusResourceKind kind, string sourceId) => _inner.IsStaged(sessionDirectory, kind, sourceId);

        public Task<System.Text.Json.Nodes.JsonObject> ReadStagedAsync(string sessionDirectory, OctopusResourceKind kind, string sourceId, CancellationToken ct = default) => _inner.ReadStagedAsync(sessionDirectory, kind, sourceId, ct);

        public Task<string> BuildBundleAsync(string sessionDirectory, string installationId, CancellationToken ct = default) => throw failure;
    }

    private sealed class SlowBundleWriter(TimeSpan delay) : IOctopusHistoryBundleWriter
    {
        private readonly OctopusHistoryBundleWriter _inner = new();

        public Action Started { get; set; }

        public Task<long> WriteDocumentAsync(string sessionDirectory, OctopusResourceKind kind, OctopusDocumentDto document, CancellationToken ct = default) => _inner.WriteDocumentAsync(sessionDirectory, kind, document, ct);

        public Task<long> WriteJsonAsync(string sessionDirectory, OctopusResourceKind kind, System.Text.Json.Nodes.JsonObject document, CancellationToken ct = default) => _inner.WriteJsonAsync(sessionDirectory, kind, document, ct);

        public bool IsStaged(string sessionDirectory, OctopusResourceKind kind, string sourceId) => _inner.IsStaged(sessionDirectory, kind, sourceId);

        public Task<System.Text.Json.Nodes.JsonObject> ReadStagedAsync(string sessionDirectory, OctopusResourceKind kind, string sourceId, CancellationToken ct = default) => _inner.ReadStagedAsync(sessionDirectory, kind, sourceId, ct);

        public async Task<string> BuildBundleAsync(string sessionDirectory, string installationId, CancellationToken ct = default)
        {
            Started?.Invoke();

            await Task.Delay(delay, ct);

            return await _inner.BuildBundleAsync(sessionDirectory, installationId, ct);
        }
    }

    // ── Staging continuity ────────────────────────────────────────────────

    // The cursor lives in the database, the staged documents on this server's disk. A chunk that finds the directory earlier chunks
    // staged into gone (a restart on ephemeral storage) or another one (another replica) refuses to go on: resuming from the cursor
    // would build a bundle silently missing everything staged before. Nothing is read from Octopus and the cursor is left as it was.
    [Theory]
    [InlineData("directory-deleted")]
    [InlineData("other-replica")]
    [InlineData("marker-of-another-fetch")]
    public async Task StagingLostBetweenChunks_ChunkRefused_NothingReadOrStaged_CursorUnchanged(string loss)
    {
        var server = new FakeOctopusServer();
        foreach (var n in Enumerable.Range(1, 3)) server.Deployment("Projects-1", n);
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = Fetcher(server, oneItemPerChunk: true);

        await fetcher.FetchChunkAsync(Context(cursor));

        cursor.StagingId.ShouldNotBeNullOrWhiteSpace();
        var otherDirectory = Path.Combine(Path.GetTempPath(), $"squid-history-fetch-{Guid.NewGuid():N}");
        var context = Context(cursor, sessionDirectory: loss == "other-replica" ? otherDirectory : null);

        try
        {
            switch (loss)
            {
                case "directory-deleted": Directory.Delete(_sessionDirectory, recursive: true); break;
                case "other-replica": break;
                default: File.WriteAllText(Path.Combine(_sessionDirectory, OctopusHistoryStagingMarker.FileName), Guid.NewGuid().ToString("N")); break;
            }

            var before = (cursor.Skip, cursor.Counts.Deployments, cursor.StagingId);
            var requests = server.Paths.Count();

            await Should.ThrowAsync<OctopusImportHistoryStagingLostException>(() => fetcher.FetchChunkAsync(context));
            await Should.ThrowAsync<OctopusImportHistoryStagingLostException>(() => fetcher.FetchChunkAsync(context));

            (cursor.Skip, cursor.Counts.Deployments, cursor.StagingId).ShouldBe(before);
            server.Paths.Count().ShouldBe(requests, "a chunk that cannot trust its staging reads nothing from Octopus");
            Directory.Exists(Path.Combine(context.SessionDirectory, OctopusHistoryBundleWriter.StagingDirectoryName)).ShouldBe(loss == "marker-of-another-fetch", "nothing is staged into a directory the fetch cannot vouch for");
        }
        finally
        {
            if (Directory.Exists(otherDirectory)) Directory.Delete(otherDirectory, recursive: true);
        }
    }

    // A cursor saved before staging was stamped (an older Squid) is stamped by its next chunk and goes on: it cannot be checked, never refused.
    [Fact]
    public async Task CursorWithoutStagingId_StampedByTheNextChunk_FetchGoesOn()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1).Deployment("Projects-1", 2);
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, Skip = 1, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };

        await Fetcher(server).FetchChunkAsync(Context(cursor));

        cursor.Done.ShouldBeTrue();
        File.ReadAllText(Path.Combine(_sessionDirectory, OctopusHistoryStagingMarker.FileName)).ShouldBe(cursor.StagingId);
    }

    // ── Lists changing while they are paged ───────────────────────────────

    // Octopus pages by offset. Removing an item already read shifts the rest one place towards the front, so continuing at the
    // same offset would pass over an unread item. A smaller total than the previous page reported steps the offset back by the
    // shrink; staged documents are overwritten by kind and id, so reading one twice is harmless and nothing is lost.
    [Theory]
    [InlineData(OctopusHistoryFetchPhase.Releases)]
    [InlineData(OctopusHistoryFetchPhase.Deployments)]
    public async Task ItemAlreadyReadRemovedWhilePaging_NoUnreadItemIsPassedOver(OctopusHistoryFetchPhase phase)
    {
        var server = new FakeOctopusServer();
        foreach (var number in new[] { 1, 2, 3 })
        {
            if (phase == OctopusHistoryFetchPhase.Releases) server.Release("Projects-1", number);
            else server.Deployment("Projects-1", number);
        }

        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = phase, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var writer = new OctopusHistoryBundleWriter();
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, writer, _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero });
        var kind = phase == OctopusHistoryFetchPhase.Releases ? OctopusResourceKind.Release : OctopusResourceKind.Deployment;
        var prefix = phase == OctopusHistoryFetchPhase.Releases ? "Releases" : "Deployments";

        await fetcher.FetchChunkAsync(Context(cursor));
        cursor.Skip.ShouldBe(1);

        if (phase == OctopusHistoryFetchPhase.Releases) server.Releases["Projects-1"].RemoveAt(0);
        else server.Deployments["Projects-1"].RemoveAt(0);

        for (var chunk = 0; chunk < 10 && cursor.Phase == phase; chunk++) await fetcher.FetchChunkAsync(Context(cursor));

        cursor.Phase.ShouldNotBe(phase);
        new[] { 1, 2, 3 }.Where(n => !writer.IsStaged(_sessionDirectory, kind, $"{prefix}-{n}")).ShouldBeEmpty("an item removed after it was read must not make the fetch pass over the next one");
    }

    // A list that grows, or keeps its size, continues where it stopped: nothing is read again.
    [Fact]
    public async Task ListGrowsWhilePaging_ContinuesAtTheSameOffset()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1).Deployment("Projects-1", 2);
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero });

        await fetcher.FetchChunkAsync(Context(cursor));
        server.Deployment("Projects-1", 3);
        await fetcher.FetchChunkAsync(Context(cursor));

        (cursor.Skip, cursor.Counts.Deployments).ShouldBe((2, 2));
    }

    // ── Abandoned items (D14) ─────────────────────────────────────────────

    // One item that keeps outliving the chunk deadline fails its chunks until the limit, then is skipped so the fetch moves on.
    // A release item reads its own process snapshot (for package feeds), so it is in flight just like a deployment item.
    [Theory]
    [InlineData(OctopusHistoryFetchPhase.Releases, "/deploymentprocesses/deploymentprocess-Projects-1-s-1")]
    [InlineData(OctopusHistoryFetchPhase.Deployments, "/events?regarding=Deployments-1")]
    public async Task ItemAbandonedByTheDeadline_FailsUntilTheLimit_ThenSkippedAndRecorded(OctopusHistoryFetchPhase phase, string stalledPath)
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1).Deployment("Projects-1", 1).Deployment("Projects-1", 2);
        server.Override = request => Uri.UnescapeDataString(request.RequestUri!.PathAndQuery).Contains(stalledPath, StringComparison.Ordinal) ? OctopusRestStubHandler.StalledBody() : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = phase, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero, ChunkOverrun = TimeSpan.FromMilliseconds(300) });

        for (var attempt = 1; attempt < OctopusRestHistoryFetcher.MaxAbandonments; attempt++)
        {
            (await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)).WaitAsync(TimeSpan.FromSeconds(30)))).Kind.ShouldBe(OctopusRestErrorKind.Unreachable);
            cursor.AbandonCounts[OctopusRestHistoryFetcher.CurrentItemKey(cursor)].ShouldBe(attempt);
        }

        await fetcher.FetchChunkAsync(Context(cursor)).WaitAsync(TimeSpan.FromSeconds(30));

        (cursor.Skip, cursor.Counts.ItemsAbandoned, cursor.Counts.Releases, cursor.Counts.Deployments).ShouldBe((1, 1, 0, 0));
        cursor.AbandonCounts.ShouldBeEmpty();
        var skipped = cursor.SkippedItems.ShouldHaveSingleItem();
        (skipped.ProjectId, skipped.Reason).ShouldBe(("Projects-1", OctopusHistoryFetchSkipReason.Abandoned));
        Uri.UnescapeDataString(skipped.Path).ShouldContain(stalledPath.TrimStart('/'));

        await fetcher.FetchChunkAsync(Context(cursor)).WaitAsync(TimeSpan.FromSeconds(30));

        // The next chunk moves past the skipped item's page instead of stalling on it again.
        cursor.Phase.ShouldNotBe(phase);
        cursor.SkippedItems.ShouldHaveSingleItem();
    }

    // A proxy that cuts the request before the chunk deadline (nginx-ingress: 60s) cancels through the request token, not the
    // deadline. An item that had already run past the chunk's soft budget is counted all the same, so a slow item is skipped
    // at the limit instead of being retried forever on every resume.
    [Fact]
    public async Task ItemCutOffByTheClientAfterOverrunningTheChunk_CountedAndSkippedAtTheLimit()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1).Deployment("Projects-1", 2);
        server.Override = request => Uri.UnescapeDataString(request.RequestUri!.PathAndQuery).Contains("/events?regarding=Deployments-1", StringComparison.Ordinal) ? OctopusRestStubHandler.StalledBody() : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero, ChunkOverrun = TimeSpan.FromMinutes(10) });

        for (var attempt = 1; attempt <= OctopusRestHistoryFetcher.MaxAbandonments; attempt++)
        {
            using var proxy = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Should.ThrowAsync<OperationCanceledException>(() => fetcher.FetchChunkAsync(Context(cursor), proxy.Token).WaitAsync(TimeSpan.FromSeconds(30)));

            if (attempt < OctopusRestHistoryFetcher.MaxAbandonments) cursor.AbandonCounts[OctopusRestHistoryFetcher.CurrentItemKey(cursor)].ShouldBe(attempt);
        }

        (cursor.Skip, cursor.Counts.ItemsAbandoned).ShouldBe((1, 1));
        cursor.AbandonCounts.ShouldBeEmpty();
        cursor.SkippedItems.ShouldHaveSingleItem().Reason.ShouldBe(OctopusHistoryFetchSkipReason.Abandoned);
    }

    // Sustained throttling (429 + Retry-After) holds the item in the server's own pauses past the chunk deadline. That overrun is
    // the server's doing, not the item's: the chunk fails retryably and the item is never counted, so throttling cannot skip history.
    [Fact]
    public async Task ItemHeldPastTheDeadlineByServerRequestedPauses_NeverCountedOrSkipped()
    {
        var server = new FakeOctopusServer { WaitsOutDelays = true }.Deployment("Projects-1", 1).Deployment("Projects-1", 2);
        server.Override = request => Uri.UnescapeDataString(request.RequestUri!.PathAndQuery).Contains("/events?regarding=Deployments-1", StringComparison.Ordinal) ? Throttled(TimeSpan.FromSeconds(1)) : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero, ChunkOverrun = TimeSpan.FromMilliseconds(1500) });

        for (var attempt = 0; attempt < OctopusRestHistoryFetcher.MaxAbandonments; attempt++)
            (await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)).WaitAsync(TimeSpan.FromSeconds(30)))).Kind.ShouldBe(OctopusRestErrorKind.Unreachable);

        (cursor.Skip, cursor.Counts.ItemsAbandoned, cursor.SkippedItems.Count).ShouldBe((0, 0, 0));
        cursor.AbandonCounts.ShouldBeEmpty("a chunk that ran out while the server had asked for a pause is not the item's fault");
    }

    // One short server pause does not excuse an item that then stalls for the rest of the overrun: it is still counted.
    [Fact]
    public async Task ItemStalledAfterAShortServerPause_StillCounted()
    {
        var throttled = 0;
        var server = new FakeOctopusServer { WaitsOutDelays = true }.Deployment("Projects-1", 1);
        server.Override = request => Uri.UnescapeDataString(request.RequestUri!.PathAndQuery).Contains("/events?regarding=Deployments-1", StringComparison.Ordinal) ? Interlocked.Increment(ref throttled) == 1 ? Throttled(TimeSpan.FromSeconds(1)) : OctopusRestStubHandler.StalledBody() : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero, ChunkOverrun = TimeSpan.FromSeconds(3) });

        (await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)).WaitAsync(TimeSpan.FromSeconds(30)))).Kind.ShouldBe(OctopusRestErrorKind.Unreachable);

        cursor.AbandonCounts[OctopusRestHistoryFetcher.CurrentItemKey(cursor)].ShouldBe(1);
    }

    private static System.Net.Http.HttpResponseMessage Throttled(TimeSpan retryAfter)
    {
        var response = OctopusRestStubHandler.Status(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        return response;
    }

    // A request the client cancels while the item is still within the chunk's budget (the user paused) is not the item's fault.
    [Fact]
    public async Task ItemCutOffByTheClientWithinTheChunkBudget_NotCounted()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        server.Override = request => Uri.UnescapeDataString(request.RequestUri!.PathAndQuery).Contains("/events?regarding=Deployments-1", StringComparison.Ordinal) ? OctopusRestStubHandler.StalledBody() : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.FromMinutes(10), ChunkOverrun = TimeSpan.FromMinutes(10) });

        for (var attempt = 0; attempt <= OctopusRestHistoryFetcher.MaxAbandonments; attempt++)
        {
            using var user = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Should.ThrowAsync<OperationCanceledException>(() => fetcher.FetchChunkAsync(Context(cursor), user.Token).WaitAsync(TimeSpan.FromSeconds(30)));
        }

        (cursor.Skip, cursor.Counts.ItemsAbandoned).ShouldBe((0, 0));
        cursor.AbandonCounts.ShouldBeEmpty();
    }

    // One item the server keeps answering with an error status (a 5xx after the client's retries, or a status the client
    // does not map such as 405/410) fails its chunks until the limit, then is skipped so one bad task cannot block the fetch.
    [Theory]
    [InlineData("/events", System.Net.HttpStatusCode.InternalServerError)]
    [InlineData("/details", System.Net.HttpStatusCode.ServiceUnavailable)]
    [InlineData("/artifacts", System.Net.HttpStatusCode.MethodNotAllowed)]
    [InlineData("/details", System.Net.HttpStatusCode.Gone)]
    public async Task ItemTheServerKeepsFailing_FailsUntilTheLimit_ThenSkippedAndRecorded_FetchFinishes(string failingPath, System.Net.HttpStatusCode status)
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1).Deployment("Projects-1", 2);
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith(failingPath, StringComparison.Ordinal) && IsFirstDeploymentsRequest(request.RequestUri) ? OctopusRestStubHandler.Status(status) : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = Fetcher(server);

        for (var attempt = 1; attempt < OctopusRestHistoryFetcher.MaxAbandonments; attempt++)
        {
            (await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)))).Kind.ShouldBe(OctopusRestErrorKind.Unreachable);
            (cursor.Skip, cursor.Counts.Deployments).ShouldBe((0, 0));
        }

        await fetcher.FetchChunkAsync(Context(cursor));
        await fetcher.FetchChunkAsync(Context(cursor));

        cursor.Done.ShouldBeTrue();
        (cursor.Counts.Deployments, cursor.Counts.ItemsAbandoned).ShouldBe((1, 1));
        cursor.AbandonCounts.ShouldBeEmpty();
        var skipped = cursor.SkippedItems.ShouldHaveSingleItem();
        (skipped.ProjectId, skipped.Reason).ShouldBe(("Projects-1", OctopusHistoryFetchSkipReason.ServerError));
        Uri.UnescapeDataString(skipped.Path).ShouldContain(failingPath.TrimStart('/'));
    }

    private static bool IsFirstDeploymentsRequest(Uri uri) => Uri.UnescapeDataString(uri.PathAndQuery) is var path && (path.Contains("ServerTasks-1/", StringComparison.Ordinal) || path.Contains("=ServerTasks-1&", StringComparison.Ordinal) || path.Contains("=Deployments-1&", StringComparison.Ordinal));

    // A server failing outside an item (its task summaries, a page) or throttling (429) is not one item's fault: nothing is skipped.
    [Theory]
    [InlineData("/api/tasks", System.Net.HttpStatusCode.InternalServerError)]
    [InlineData("/api/Spaces-1/deployments", System.Net.HttpStatusCode.BadGateway)]
    [InlineData("/events", System.Net.HttpStatusCode.TooManyRequests)]
    public async Task ServerFailingOutsideAnItemOrThrottling_NeverSkipsAnything(string failingPath, System.Net.HttpStatusCode status)
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith(failingPath, StringComparison.Ordinal) ? OctopusRestStubHandler.Status(status) : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = Fetcher(server);

        for (var attempt = 0; attempt <= OctopusRestHistoryFetcher.MaxAbandonments; attempt++)
            (await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)))).Kind.ShouldBe(OctopusRestErrorKind.Unreachable);

        (cursor.Skip, cursor.Counts.ItemsAbandoned, cursor.SkippedItems.Count).ShouldBe((0, 0, 0));
        cursor.AbandonCounts.ShouldBeEmpty();
    }

    // Stuck outside an item (the key's identity, a page of deployments, the page's task summaries) no item is at fault,
    // so none is skipped: skipping the first item of the page would drop healthy history. It always fails instead.
    [Theory]
    [InlineData("/api/users/me")]
    [InlineData("/api/Spaces-1/deployments")]
    [InlineData("/api/tasks")]
    public async Task ChunkAbandonedOutsideAnItem_NeverSkipsAnything(string stalledPath)
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        server.Override = request => request.RequestUri!.AbsolutePath == stalledPath ? OctopusRestStubHandler.StalledBody() : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero, ChunkOverrun = TimeSpan.FromMilliseconds(300) });

        for (var attempt = 0; attempt <= OctopusRestHistoryFetcher.MaxAbandonments; attempt++)
            (await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)).WaitAsync(TimeSpan.FromSeconds(30)))).Kind.ShouldBe(OctopusRestErrorKind.Unreachable);

        (cursor.Skip, cursor.Counts.ItemsAbandoned).ShouldBe((0, 0));
        cursor.AbandonCounts.ShouldBeEmpty();
    }

    // An item committed earlier in the same chunk does not make a later page request count as an item in flight.
    [Fact]
    public async Task PageStalledAfterAnItemOfTheSameChunk_NotCountedAgainstAnyItem()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1).Deployment("Projects-1", 1);
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/deployments", StringComparison.Ordinal) ? OctopusRestStubHandler.StalledBody() : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.FromSeconds(1), ChunkOverrun = TimeSpan.FromMilliseconds(300) });

        (await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)).WaitAsync(TimeSpan.FromSeconds(30)))).Kind.ShouldBe(OctopusRestErrorKind.Unreachable);

        (cursor.Counts.Releases, cursor.Phase).ShouldBe((1, OctopusHistoryFetchPhase.Deployments));
        cursor.AbandonCounts.ShouldBeEmpty();
    }

    // Every read of a deployment unit happens before its first write, so an item abandoned part way (here on its last
    // detail list, after its task details) leaves nothing of itself in the bundle.
    [Fact]
    public async Task ItemAbandonedAfterItsDetails_LeavesNoDocumentOfItselfInTheBundle()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1).Deployment("Projects-1", 2);
        var stalls = 0;
        server.Override = request => request.RequestUri!.Query.Contains("regarding=ServerTasks-1", StringComparison.Ordinal) && request.RequestUri.AbsolutePath.EndsWith("/artifacts", StringComparison.Ordinal) && stalls++ < OctopusRestHistoryFetcher.MaxAbandonments ? OctopusRestStubHandler.StalledBody() : null;
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero, ChunkOverrun = TimeSpan.FromMilliseconds(300) });

        for (var attempt = 1; attempt < OctopusRestHistoryFetcher.MaxAbandonments; attempt++)
            await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor)).WaitAsync(TimeSpan.FromSeconds(30)));

        while (!cursor.Done) await fetcher.FetchChunkAsync(Context(cursor)).WaitAsync(TimeSpan.FromSeconds(30));

        (cursor.Counts.ItemsAbandoned, cursor.Counts.Deployments, cursor.Counts.ServerTasks).ShouldBe((1, 1, 1));
        var bundled = (await BundleGraphAsync()).Resources.Select(r => r.SourceId).ToList();
        bundled.ShouldContain("ServerTasks-2");
        bundled.ShouldNotContain("ServerTasks-1");
        bundled.ShouldNotContain("Deployments-1");
        (await _detailsStore.ReadAsync(_sessionDirectory, "ServerTasks-1")).ShouldBeNull();
    }

    [Fact]
    public void AbandonmentLimits_Pinned()
    {
        (OctopusRestHistoryFetcher.MaxAbandonments, OctopusRestHistoryFetcher.MaxRecordedSkippedItems).ShouldBe((3, 500));
    }

    [Fact]
    public async Task TaskDetails_AskOnlyForTheLogTailTheImporterKeeps()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);

        await Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), projects: ["Projects-1"]));

        server.Paths.ShouldContain($"/api/Spaces-1/tasks/ServerTasks-1/details?verbose=false&tail={OctopusImportDeploymentHistoryImporter.MaxLogLinesPerTask()}");
    }

    // One task whose log is over the per-response cap must not fail the whole history: it is imported without details.
    [Fact]
    public async Task TaskDetailsOverTheResponseCap_TaskKeptWithoutDetails_FetchContinues()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1).Deployment("Projects-1", 2);
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith("ServerTasks-1/details", StringComparison.Ordinal) ? DeclaredTooLarge() : null;

        var progress = await Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), projects: ["Projects-1"]));

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

        var progress = await Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), projects: ["Projects-1", "Projects-9"]));

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
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions(), new OctopusImportSizeLimits { MaxBytes = 50_000 });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), projects: ["Projects-1"])));

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
            await fetcher.FetchChunkAsync(Context(cursor, projects: ["Projects-1"]));
            phases.Add((cursor.Phase, cursor.ProjectIndex));
        }

        // After the last project: the closure, one kind per chunk (the environment, the package feed and the channel the
        // history names, none staged here), then the bundle.
        phases.ShouldBe([(OctopusHistoryFetchPhase.Deployments, 0), (OctopusHistoryFetchPhase.Closure, 1), (OctopusHistoryFetchPhase.Closure, 1), (OctopusHistoryFetchPhase.Closure, 1), (OctopusHistoryFetchPhase.Closure, 1), (OctopusHistoryFetchPhase.Done, 1)]);
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

        await fetcher.FetchChunkAsync(Context(cursor, projects: ["Projects-1"]));
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

        await Fetcher(server).FetchChunkAsync(Context(cursor, projects: ["Projects-1"]));

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

        await Fetcher(server).FetchChunkAsync(Context(cursor, projects: ["Projects-1"]));

        cursor.OctopusSpaceId.ShouldBe("Spaces-1");
        server.Paths.ShouldContain("/api/Spaces-1/projects/Projects-1/releases?skip=0&take=100");
        server.Paths.ShouldContain(p => p.StartsWith("/api/tasks?") || p.StartsWith("/api/Spaces-1/deployments"));
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

        await Fetcher(server).FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), projects: ["Projects-1"]));

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
        deployment.ExtensionData[OctopusRestHistoryFetcher.DeployedToMachineNamesProperty].Deserialize<string[]>().ShouldBe(["Machines-1", "Machines-9"], "no machine document was staged, so each machine keeps its id as its name");
    }

    // Transfer is not storage: snapshots read for feed ids, task details, reads ahead a chunk discards and re-reads after an
    // abandoned chunk are never staged. A fetch whose earlier chunks read close to the cap goes on while what it stores fits.
    [Fact]
    public async Task BytesReadByEarlierChunks_NotChargedAgainstTheStorageCap()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1).Deployment("Projects-1", 1);
        const long cap = 10_000_000;
        var cursor = new OctopusImportHistoryFetchDto { TotalBytes = cap - 10 };
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions(), new OctopusImportSizeLimits { MaxBytes = cap });

        await fetcher.FetchChunkAsync(Context(cursor, projects: ["Projects-1"]));

        (cursor.Counts.Releases, cursor.Counts.Deployments).ShouldBe((1, 1));
        cursor.TotalBytes.ShouldBeGreaterThan(cap, "the progress still reports every byte read");
    }

    // Task details are stored beside the bundle, outside the staging cap: their stored size is counted and capped on its own.
    [Fact]
    public async Task StoredTaskDetails_CountedAndCapped()
    {
        var server = new FakeOctopusServer().Deployment("Projects-1", 1);
        var cursor = new OctopusImportHistoryFetchDto();

        await Fetcher(server).FetchChunkAsync(Context(cursor, projects: ["Projects-1"]));

        var stored = Directory.GetFiles(Path.Combine(_sessionDirectory, OctopusImportHistoryDetailsStore.DirectoryName)).Sum(f => new FileInfo(f).Length);
        stored.ShouldBeGreaterThan(0);
        cursor.DetailsBytes.ShouldBe(stored);

        var full = new OctopusImportHistoryFetchDto { DetailsBytes = 10_000_000 - 1 };
        var capped = new OctopusRestHistoryFetcher(new FakeOctopusServer().Deployment("Projects-1", 2).Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions(), new OctopusImportSizeLimits { MaxBytes = 10_000_000 });

        (await Should.ThrowAsync<OctopusRestException>(() => capped.FetchChunkAsync(Context(full, projects: ["Projects-1"])))).Kind.ShouldBe(OctopusRestErrorKind.TooLarge);
    }

    [Fact]
    public async Task HistoryOverTheByteCap_TooLarge()
    {
        var server = new FakeOctopusServer().Release("Projects-1", 1);
        var fetcher = new OctopusRestHistoryFetcher(server.Factory, new OctopusHistoryBundleWriter(), _detailsStore, new OctopusRestOptions(), new OctopusImportSizeLimits { MaxBytes = 64 });

        var ex = await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(new OctopusImportHistoryFetchDto(), projects: ["Projects-1"])));

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

    // These tests start past the catalog and the project's configuration: a cursor without projects is given Projects-1
    // (or the projects named), at the releases phase.
    private OctopusHistoryFetchContext Context(OctopusImportHistoryFetchDto cursor, OctopusImportPriorMappings prior = null, IReadOnlyList<string> projects = null, string apiKey = FakeOctopusServer.ApiKey, string sessionDirectory = null)
    {
        if (cursor.ProjectIds.Count == 0) cursor.ProjectIds = [.. projects ?? ["Projects-1"]];

        return new() { ServerUrl = FakeOctopusServer.Url, ApiKey = apiKey, SessionDirectory = sessionDirectory ?? _sessionDirectory, Cursor = cursor, PriorMappings = prior ?? OctopusImportPriorMappings.Empty };
    }

    private static OctopusImportPriorMappings Prior(params (string Type, string Id, string Fingerprint, string DetailFingerprint)[] rows) => Prior("inst-1", rows);

    private static OctopusImportPriorMappings Prior(string installationId, params (string Type, string Id, string Fingerprint, string DetailFingerprint)[] rows)
        => new(rows.ToDictionary(r => (r.Type, r.Id), r => new OctopusImportPriorMapping(1, "Created", r.Fingerprint, r.DetailFingerprint)), installationId);

    private async Task<OctopusResourceGraph> BundleGraphAsync()
    {
        var bundlePath = Path.Combine(_sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName);
        var extraction = await new OctopusImportSourceReader(new OctopusArchiveExtractor(), new OctopusInputExtractor()).ReadAsync(bundlePath);

        return new OctopusResourceGraphBuilder().Build(new OctopusManifestInventoryBuilder().Build(extraction));
    }

    private async Task<OctopusServerTaskDto> BundledTaskAsync(string taskId) => (await BundleGraphAsync()).Resources.Single(r => r.SourceId == taskId).GetSource<OctopusServerTaskDto>();

    private OctopusExportManifestDto ReadBundleManifest()
    {
        using var zip = ZipFile.OpenRead(Path.Combine(_sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName));
        using var manifest = zip.GetEntry("manifest.json")!.Open();

        return JsonSerializer.Deserialize<OctopusExportManifestDto>(manifest);
    }
}
