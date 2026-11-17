using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Rest;

/// <summary>
/// Read ahead, write in order: the fetch reads up to <see cref="OctopusRestOptions.Concurrency"/> history items ahead
/// (and a deployment's detail lists side by side) but writes and commits them one by one in page order, so what it
/// stages, bundles and records is exactly what a one-request-at-a-time fetch produces.
/// </summary>
public sealed class OctopusRestFetchConcurrencyTests : IDisposable
{
    private readonly List<string> _sessionDirectories = [];

    // Over 3 in flight proves deployments are read side by side (one deployment has at most its three detail lists in flight).
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task Deployments_ReadAhead_InFlightBoundedByConcurrency(int concurrency)
    {
        var server = Server(concurrency, deployments: 40);
        server.Handler.Latency = _ => TimeSpan.FromMilliseconds(10);

        var cursor = await FetchUntilDoneAsync(server, Options(concurrency));

        cursor.Counts.Deployments.ShouldBe(40);

        if (concurrency == 1)
            server.Handler.MaxInFlight.ShouldBe(1, customMessage: "concurrency 1 is the old one-request-at-a-time fetch");
        else
            server.Handler.MaxInFlight.ShouldBeInRange(4, concurrency, "deployments must be read ahead, and never beyond the limit");
    }

    // Many items, several pages, items whose lists Octopus refuses (two in one item, so their order within it matters),
    // tasks over the response cap, and answers that arrive out of order: the staged session,
    // the bundle, the cursor (counts, skipped items in order) and the closure requests are identical at concurrency 1 and 8.
    [Theory]
    [InlineData(60_000)]
    [InlineData(40)]
    public async Task ConcurrentFetch_StagesAndRecordsExactlyWhatTheSequentialFetchDoes(int chunkMilliseconds)
    {
        var sequential = Server(1, deployments: 230, releases: 130);
        var concurrent = Server(8, deployments: 230, releases: 130);

        var sequentialCursor = await FetchUntilDoneAsync(sequential, Options(1, chunkMilliseconds));
        var concurrentCursor = await FetchUntilDoneAsync(concurrent, Options(8, chunkMilliseconds));

        concurrent.Handler.MaxInFlight.ShouldBeGreaterThan(1);
        sequentialCursor.Counts.ItemsRejected.ShouldBeGreaterThan(20, "the scenario must exercise refusals, or the comparison proves little");
        sequentialCursor.Counts.DetailsTooLarge.ShouldBeGreaterThan(5);
        CursorJson(concurrentCursor, ignoreBytes: chunkMilliseconds < 60_000).ShouldBe(CursorJson(sequentialCursor, ignoreBytes: chunkMilliseconds < 60_000));
        ClosureRequests(concurrent).ShouldBe(ClosureRequests(sequential));
        SessionFiles(_sessionDirectories[1]).ShouldBe(SessionFiles(_sessionDirectories[0]));
    }

    // The chunk is abandoned on the item it is stuck on while later items were already read ahead: none of those is
    // staged, the cursor stays on the stuck item, and once it has been skipped every other deployment is fetched.
    [Fact]
    public async Task AbandonedMidPage_ReadAheadItemsLeaveNothing_StuckItemSkippedAtTheLimit()
    {
        var server = Server(8, deployments: 12);
        var stalls = 0;
        server.Override = request => Uri.UnescapeDataString(request.RequestUri!.PathAndQuery).Contains("/events?regarding=Deployments-4&", StringComparison.Ordinal) && Interlocked.Increment(ref stalls) <= OctopusRestHistoryFetcher.MaxAbandonments ? OctopusRestStubHandler.StalledBody() : null;
        var cursor = DeploymentsCursor();
        var options = new OctopusRestOptions { Concurrency = 8, ChunkDuration = TimeSpan.FromSeconds(1), ChunkOverrun = TimeSpan.FromMilliseconds(300) };
        var fetcher = Fetcher(server, options);
        var session = NewSession();

        (await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor, session)).WaitAsync(TimeSpan.FromSeconds(30)))).Kind.ShouldBe(OctopusRestErrorKind.Unreachable);

        (cursor.Skip, cursor.Counts.Deployments).ShouldBe((3, 3));
        server.Paths.ShouldContain(p => p.Contains("regarding=ServerTasks-8"), "later deployments were read ahead while the fourth was stuck");
        Staged(session, OctopusResourceKind.Deployment, "Deployments").ShouldBe(["Deployments-1", "Deployments-2", "Deployments-3"]);
        Staged(session, OctopusResourceKind.ServerTask, "ServerTasks").ShouldBe(["ServerTasks-1", "ServerTasks-2", "ServerTasks-3"]);

        for (var attempt = 2; attempt < OctopusRestHistoryFetcher.MaxAbandonments; attempt++)
            await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor, session)).WaitAsync(TimeSpan.FromSeconds(30)));

        while (!cursor.Done) await fetcher.FetchChunkAsync(Context(cursor, session)).WaitAsync(TimeSpan.FromSeconds(30));

        (cursor.Counts.Deployments, cursor.Counts.ItemsAbandoned).ShouldBe((11, 1));
        var skipped = cursor.SkippedItems.ShouldHaveSingleItem();
        Uri.UnescapeDataString(skipped.Path).ShouldContain("events?regarding=Deployments-4", customMessage: "the skipped item names the request it was stuck on, not whichever request was made last");
        new OctopusHistoryBundleWriter().IsStaged(session, OctopusResourceKind.Deployment, "Deployments-4").ShouldBeFalse();
    }

    // Requests of deployments read ahead (made before the stuck one) are still waiting when the chunk is abandoned: the
    // skipped item names the request its own deployment was stuck on, not the oldest request of any item.
    [Fact]
    public async Task AbandonedMidPage_OtherItemsStillWaiting_SkippedItemNamesItsOwnStuckRequest()
    {
        var server = Server(8, deployments: 12);
        var (eventStalls, detailStalls) = (0, 0);
        server.Override = request => Uri.UnescapeDataString(request.RequestUri!.PathAndQuery) switch
        {
            var p when p.Contains("/events?regarding=Deployments-4&", StringComparison.Ordinal) && Interlocked.Increment(ref eventStalls) <= OctopusRestHistoryFetcher.MaxAbandonments => OctopusRestStubHandler.StalledBody(),
            var p when p.Contains("/tasks/ServerTasks-6/details?", StringComparison.Ordinal) && Interlocked.Increment(ref detailStalls) <= OctopusRestHistoryFetcher.MaxAbandonments => OctopusRestStubHandler.StalledBody(),
            _ => null
        };
        var cursor = DeploymentsCursor();
        var fetcher = Fetcher(server, new OctopusRestOptions { Concurrency = 8, ChunkDuration = TimeSpan.FromSeconds(1), ChunkOverrun = TimeSpan.FromMilliseconds(300) });
        var session = NewSession();

        for (var attempt = 1; attempt < OctopusRestHistoryFetcher.MaxAbandonments; attempt++)
            await Should.ThrowAsync<OctopusRestException>(() => fetcher.FetchChunkAsync(Context(cursor, session)).WaitAsync(TimeSpan.FromSeconds(30)));

        while (!cursor.Done) await fetcher.FetchChunkAsync(Context(cursor, session)).WaitAsync(TimeSpan.FromSeconds(30));

        (cursor.Counts.Deployments, cursor.Counts.ItemsAbandoned).ShouldBe((11, 1));
        Uri.UnescapeDataString(cursor.SkippedItems.ShouldHaveSingleItem().Path).ShouldContain("events?regarding=Deployments-4", customMessage: "ServerTasks-6/details was older and still waiting, but it is another deployment's request");
    }

    // A detail list that fails ends its deployment's read at once: the sibling list stuck on a slow answer is cut off, not
    // waited for until the chunk deadline, and the failure is the failed list's own.
    [Fact]
    public async Task DetailListFails_SiblingListsCancelled_FailureReportedAtOnce()
    {
        var server = Server(8, deployments: 1);
        server.Override = request => request.RequestUri!.AbsolutePath.Split('/')[^1] switch
        {
            "interruptions" => OctopusRestStubHandler.Status(HttpStatusCode.Unauthorized),
            "events" => OctopusRestStubHandler.StalledBody(),
            _ => null
        };
        var options = new OctopusRestOptions { Concurrency = 8, ChunkDuration = TimeSpan.FromSeconds(30), ChunkOverrun = TimeSpan.FromSeconds(30) };

        var failure = await Should.ThrowAsync<OctopusRestException>(() => Fetcher(server, options).FetchChunkAsync(Context(DeploymentsCursor(), NewSession())).WaitAsync(TimeSpan.FromSeconds(10)));

        failure.Kind.ShouldBe(OctopusRestErrorKind.Unauthorized);
    }

    // Items already read ahead when the chunk's time budget runs out are written and committed before the chunk returns,
    // not dropped and read again by the next chunk: one slow item does not cost the reads of the items behind it.
    [Fact]
    public async Task BudgetSpentWhileReadsAheadAreDone_CompletedReadsCommitted_NoneReadTwice()
    {
        var server = Server(8, deployments: 12);
        server.Override = null;
        server.Handler.Latency = request => Uri.UnescapeDataString(request.RequestUri!.PathAndQuery).Contains("/events?regarding=Deployments-1&", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(600) : TimeSpan.Zero;
        var cursor = DeploymentsCursor();
        var fetcher = Fetcher(server, new OctopusRestOptions { Concurrency = 8, ChunkDuration = TimeSpan.FromMilliseconds(300), ChunkOverrun = TimeSpan.FromSeconds(30) });
        var session = NewSession();

        await fetcher.FetchChunkAsync(Context(cursor, session)).WaitAsync(TimeSpan.FromSeconds(30));

        (cursor.Skip, cursor.Counts.Deployments).ShouldBe((8, 8), "the seven deployments read ahead behind the slow first one were already read");

        while (!cursor.Done) await fetcher.FetchChunkAsync(Context(cursor, session)).WaitAsync(TimeSpan.FromSeconds(30));

        cursor.Counts.Deployments.ShouldBe(12);
        server.Paths.Count(p => p.Contains("/events?")).ShouldBe(12, "no deployment's lists are read twice");
    }

    // A 429's Retry-After is still honoured with reads in flight side by side, and every throttled read is retried, not lost.
    [Fact]
    public async Task Throttled_RetryAfterHonoured_EveryItemStillFetched()
    {
        var server = Server(8, deployments: 20);
        var throttles = 0;
        server.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) && Interlocked.Increment(ref throttles) <= 3 ? TooManyRequests(TimeSpan.FromSeconds(3)) : null;

        var cursor = await FetchUntilDoneAsync(server, Options(8));

        (cursor.Counts.Deployments, cursor.Counts.DetailsFetched, cursor.Counts.ItemsRejected).ShouldBe((20, 20, 0));
        server.Delays.Count(d => d > TimeSpan.FromSeconds(2) && d <= TimeSpan.FromSeconds(3)).ShouldBeGreaterThanOrEqualTo(3);
        server.Paths.Count(p => p.Contains("/events?")).ShouldBe(23);
    }

    // Releases sharing a process snapshot read it once per chunk, however many of them are read side by side.
    [Fact]
    public async Task Releases_SharedProcessSnapshot_ReadOncePerChunk()
    {
        var server = Server(8, deployments: 0);
        foreach (var n in Enumerable.Range(1, 60)) server.Release("Projects-1", n);
        server.Handler.Latency = _ => TimeSpan.FromMilliseconds(5);

        var cursor = await FetchUntilDoneAsync(server, Options(8), new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" });

        cursor.Counts.Releases.ShouldBe(60);
        server.Paths.Count(p => p.Contains("/deploymentprocesses/")).ShouldBe(1);
    }

    // A deployment's detail lists are read side by side, but what they refuse is recorded in the order a one-by-one read
    // asks for them (interruptions, events, artifacts) however the answers arrive.
    [Fact]
    public async Task DetailListsReadSideBySide_RefusalsRecordedInTheOneByOneOrder()
    {
        var server = Server(8, deployments: 1);
        server.Override = request => request.RequestUri!.AbsolutePath.Split('/')[^1] is "interruptions" or "events" or "artifacts" ? OctopusRestStubHandler.Status(HttpStatusCode.Forbidden) : null;
        server.Handler.Latency = request => request.RequestUri!.AbsolutePath.EndsWith("/interruptions", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(60) : request.RequestUri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(30) : TimeSpan.Zero;

        var cursor = await FetchUntilDoneAsync(server, Options(8), DeploymentsCursor());

        cursor.SkippedItems.Select(i => i.Path.Split('?')[0].Split('/')[^1]).ShouldBe(["interruptions", "events", "artifacts"]);
    }

    // ── Progress ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Progress_ReportsItemsDoneOfTheTotalOctopusReports_PerPhase()
    {
        var server = Server(8, deployments: 150);
        foreach (var n in Enumerable.Range(1, 3)) server.Release("Projects-1", n);
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };
        var fetcher = Fetcher(server, new OctopusRestOptions { ChunkDuration = TimeSpan.Zero });
        var session = NewSession();

        var releases = await fetcher.FetchChunkAsync(Context(cursor, session));

        (releases.Phase, releases.PhaseItemsDone, releases.PhaseItemsTotal).ShouldBe((OctopusHistoryFetchPhase.Releases, 1, 3));

        while (cursor.Phase == OctopusHistoryFetchPhase.Releases) await fetcher.FetchChunkAsync(Context(cursor, session));

        OctopusRestHistoryFetcher.Progress(cursor).PhaseItemsTotal.ShouldBeNull("the deployments' total is unknown until their first page is read");

        var deployments = await fetcher.FetchChunkAsync(Context(cursor, session));

        (deployments.Phase, deployments.PhaseItemsDone, deployments.PhaseItemsTotal).ShouldBe((OctopusHistoryFetchPhase.Deployments, 1, 150));

        while (!cursor.Done) await fetcher.FetchChunkAsync(Context(cursor, session));

        var done = OctopusRestHistoryFetcher.Progress(cursor);
        (done.PhaseItemsDone, done.PhaseItemsTotal).ShouldBe((0, null));
    }

    // The catalog keeps its own total; a cursor persisted before this change (no phase total) reports the total as unknown.
    [Theory]
    [InlineData(OctopusHistoryFetchPhase.Catalog, 40, null, 7, 7, 40)]
    [InlineData(OctopusHistoryFetchPhase.Releases, null, null, 7, 0, null)]
    [InlineData(OctopusHistoryFetchPhase.Releases, null, 4990, 300, 300, 4990)]
    [InlineData(OctopusHistoryFetchPhase.Deployments, null, 4990, 120, 120, 4990)]
    [InlineData(OctopusHistoryFetchPhase.ProjectConfiguration, null, 4990, 5, 0, null)]
    [InlineData(OctopusHistoryFetchPhase.Closure, null, 4990, 3, 0, null)]
    [InlineData(OctopusHistoryFetchPhase.Done, 40, 4990, 3, 0, null)]
    public void Progress_PhaseItems(OctopusHistoryFetchPhase phase, int? catalogTotal, int? phaseTotal, int skip, int expectedDone, int? expectedTotal)
    {
        var cursor = new OctopusImportHistoryFetchDto { Phase = phase, Skip = skip, CatalogTotalResults = catalogTotal, PhaseTotalResults = phaseTotal };

        var progress = OctopusRestHistoryFetcher.Progress(cursor);

        (progress.PhaseItemsDone, progress.PhaseItemsTotal).ShouldBe((expectedDone, expectedTotal));
    }

    public void Dispose()
    {
        foreach (var directory in _sessionDirectories.Where(Directory.Exists)) Directory.Delete(directory, recursive: true);
    }

    // ── Scenario ───────────────────────────────────────────────────────────

    // Answers arrive out of order (a latency that varies by request), and every so often a list is refused or a task's log is
    // over the response cap, so skipped items and counts come from many items. (A refused process snapshot is recorded once
    // per chunk by design, so it would differ with where chunks end; OctopusRestHistoryFetcherTests covers it.)
    private static FakeOctopusServer Server(int concurrency, int deployments, int releases = 0)
    {
        var server = new FakeOctopusServer { ClientOptions = new OctopusRestOptions { Concurrency = concurrency } };

        foreach (var n in Enumerable.Range(1, deployments)) server.Deployment("Projects-1", n);
        server.Releases.TryAdd("Projects-1", []);
        foreach (var n in Enumerable.Range(1, releases)) server.Releases["Projects-1"].Add(new { Id = $"Releases-{n}", ProjectId = "Projects-1", ChannelId = $"Channels-{n % 4}", Version = $"1.0.{n}", ProjectDeploymentProcessSnapshotId = $"deploymentprocess-Projects-1-s-{n % 3}", SelectedPackages = new[] { new { ActionName = "Deploy web", PackageReferenceName = "", Version = "1.0.0" } } });
        foreach (var (deployment, index) in server.Deployments.GetValueOrDefault("Projects-1", []).Select((d, i) => (d, i))) (deployment["EnvironmentId"], deployment["ChannelId"]) = ($"Environments-{index % 7}", $"Channels-{index % 5}");

        server.Override = Faults;
        server.Handler.Latency = request => TimeSpan.FromMilliseconds(Stable(request.RequestUri!.PathAndQuery) % 4);

        return server;
    }

    private static HttpResponseMessage Faults(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var number = Number(Uri.UnescapeDataString(request.RequestUri.Query + path));

        if (path.EndsWith("/details", StringComparison.Ordinal) && number % 19 == 0) return DeclaredTooLarge();
        if (path.EndsWith("/interruptions", StringComparison.Ordinal) && number % 13 == 0) return OctopusRestStubHandler.Status(HttpStatusCode.Forbidden);
        if (path.EndsWith("/events", StringComparison.Ordinal) && number % 11 == 0) return OctopusRestStubHandler.Status(HttpStatusCode.Conflict);
        if (path.EndsWith("/artifacts", StringComparison.Ordinal) && number % 13 == 0) return OctopusRestStubHandler.Status(HttpStatusCode.UnprocessableEntity);

        return null;
    }

    // The number of the deployment or task a detail request is about ("ServerTasks-26", "regarding=Deployments-26").
    private static int Number(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?:ServerTasks|Deployments)-(\d+)");

        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    private static int Stable(string text) => text.Aggregate(17, (hash, c) => unchecked(hash * 31 + c)) & int.MaxValue;

    private static HttpResponseMessage DeclaredTooLarge()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Content.Headers.ContentLength = new OctopusRestOptions().MaxResponseBytes + 1;
        return response;
    }

    private static HttpResponseMessage TooManyRequests(TimeSpan retryAfter)
    {
        var response = OctopusRestStubHandler.Status(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        return response;
    }

    // ── Fetching ───────────────────────────────────────────────────────────

    private static OctopusRestOptions Options(int concurrency, int chunkMilliseconds = 60_000) => new() { Concurrency = concurrency, ChunkDuration = TimeSpan.FromMilliseconds(chunkMilliseconds) };

    private OctopusRestHistoryFetcher Fetcher(FakeOctopusServer server, OctopusRestOptions options) => new(server.Factory, new OctopusHistoryBundleWriter(), new OctopusImportHistoryDetailsStore(), options);

    private async Task<OctopusImportHistoryFetchDto> FetchUntilDoneAsync(FakeOctopusServer server, OctopusRestOptions options, OctopusImportHistoryFetchDto cursor = null)
    {
        var fetcher = Fetcher(server, options);
        var session = NewSession();

        cursor ??= new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };

        while (!cursor.Done) await fetcher.FetchChunkAsync(Context(cursor, session)).WaitAsync(TimeSpan.FromSeconds(60));

        return cursor;
    }

    private static OctopusImportHistoryFetchDto DeploymentsCursor() => new() { ProjectIds = ["Projects-1"], Phase = OctopusHistoryFetchPhase.Deployments, InstallationId = "inst-1", OctopusSpaceId = "Spaces-1" };

    private string NewSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"squid-history-concurrency-{Guid.NewGuid():N}");

        _sessionDirectories.Add(directory);

        return directory;
    }

    private static OctopusHistoryFetchContext Context(OctopusImportHistoryFetchDto cursor, string session)
        => new() { ServerUrl = FakeOctopusServer.Url, ApiKey = FakeOctopusServer.ApiKey, SessionDirectory = session, Cursor = cursor, PriorMappings = OctopusImportPriorMappings.Empty };

    // ── Comparison ─────────────────────────────────────────────────────────

    // Bytes read include reads made ahead for items a spent chunk never reached; they are read again by the next chunk.
    private static string CursorJson(OctopusImportHistoryFetchDto cursor, bool ignoreBytes)
    {
        var node = JsonSerializer.SerializeToNode(cursor)!.AsObject();

        foreach (var name in new[] { "StartedAt", "CompletedAt", "ChunkId", "LeaseExpiresAt", "StagingId" }) node.Remove(name);

        if (ignoreBytes) node.Remove("TotalBytes");

        return node.ToJsonString();
    }

    private static List<string> Staged(string session, OctopusResourceKind kind, string prefix)
        => Enumerable.Range(1, 12).Select(n => $"{prefix}-{n}").Where(id => new OctopusHistoryBundleWriter().IsStaged(session, kind, id)).ToList();

    private static List<string> ClosureRequests(FakeOctopusServer server) => server.Paths.Where(p => p.Contains("ids=") && !p.StartsWith("/api/tasks", StringComparison.Ordinal)).ToList();

    // The staging id is random per fetch (it ties the cursor to its directory), so it is the one file two fetches never share.
    private static List<string> SessionFiles(string session)
        => Directory.EnumerateFiles(session, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != OctopusHistoryStagingMarker.FileName).Order(StringComparer.Ordinal).Select(f => $"{Path.GetRelativePath(session, f)}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))}").ToList();
}
