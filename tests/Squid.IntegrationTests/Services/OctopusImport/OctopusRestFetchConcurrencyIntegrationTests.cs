using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Xunit.Abstractions;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// High-fidelity tier for the read-ahead of the REST history fetch: the production client factory (real sockets, the
/// address guard, the connection pool), fetcher and bundle writer against a real-socket stub Octopus that answers with a
/// latency and counts the requests it serves at once. No database: the fetch only stages files in the session directory.
/// </summary>
[Trait("Category", "OctopusImportRest")]
public sealed class OctopusRestFetchConcurrencyIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private const string ApiKey = "API-STUBKEY0000000000000001";

    private readonly List<string> _sessionDirectories = [];

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task ReadAhead_OverRealSockets_InFlightBoundedByConcurrency(int concurrency)
    {
        await using var octopus = Octopus(deployments: 60);
        octopus.Latency = TimeSpan.FromMilliseconds(20);

        var cursor = await FetchUntilDoneAsync(octopus, concurrency);

        cursor.Counts.Deployments.ShouldBe(60);

        if (concurrency == 1)
            octopus.MaxInFlight.ShouldBe(1, customMessage: "concurrency 1 must be the old one-request-at-a-time fetch; check the client's request slots");
        else
            octopus.MaxInFlight.ShouldBeInRange(4, concurrency, "deployments must be read ahead side by side and never beyond the limit; check ReadAheadWidth and the client's request slots");
    }

    [Fact]
    public async Task ConcurrentFetch_OverRealSockets_StagesExactlyWhatTheSequentialFetchDoes()
    {
        await using var sequential = Octopus(deployments: 130, releases: 40);
        await using var concurrent = Octopus(deployments: 130, releases: 40);
        sequential.Latency = concurrent.Latency = TimeSpan.FromMilliseconds(2);

        var sequentialCursor = await FetchUntilDoneAsync(sequential, 1);
        var concurrentCursor = await FetchUntilDoneAsync(concurrent, 8);

        concurrent.MaxInFlight.ShouldBeGreaterThan(1);
        (concurrentCursor.Counts.Releases, concurrentCursor.Counts.Deployments, concurrentCursor.Counts.DetailsFetched).ShouldBe((40, 130, 130));
        CursorJson(concurrentCursor).ShouldBe(CursorJson(sequentialCursor));
        SessionFiles(_sessionDirectories[1]).ShouldBe(SessionFiles(_sessionDirectories[0]));
    }

    // A 429 pauses the whole fetch, not only the throttled request: nothing else reaches the server until Retry-After has passed.
    [Fact]
    public async Task Throttled_OverRealSockets_RetryAfterPausesEveryRequest_AndTheFetchCompletes()
    {
        await using var octopus = Octopus(deployments: 40).ThrottleNext("/events", 1, TimeSpan.FromSeconds(1));
        octopus.Latency = TimeSpan.FromMilliseconds(10);

        var cursor = await FetchUntilDoneAsync(octopus, 8);

        (cursor.Counts.Deployments, cursor.Counts.DetailsFetched, cursor.Counts.ItemsRejected).ShouldBe((40, 40, 0));
        var throttledAt = octopus.ThrottledAtMs.ShouldHaveSingleItem();
        var duringPause = octopus.Arrivals.Where(a => a.ArrivedMs > throttledAt + 150 && a.ArrivedMs < throttledAt + 850).ToList();
        duringPause.ShouldBeEmpty(customMessage: $"requests reached the server while it had asked for a pause: {string.Join(", ", duringPause.Select(a => a.PathAndQuery))}");
        octopus.Arrivals.ShouldContain(a => a.ArrivedMs >= throttledAt + 900, "the fetch resumes after Retry-After");
    }

    // Measurement, not a gate on a number: reads at 40 ms per request, one at a time vs eight in flight. The assertion is
    // only that reading ahead is clearly faster; the rates are written to the test output.
    [Fact]
    public async Task Throughput_FortyMillisecondLatency_ConcurrencyEightClearlyFasterThanOne()
    {
        var sequential = await DeploymentsPerSecondAsync(1);
        var concurrent = await DeploymentsPerSecondAsync(8);

        output.WriteLine($"40 ms/request: concurrency 1 = {sequential:F1} deployments/s, concurrency 8 = {concurrent:F1} deployments/s, speedup {concurrent / sequential:F1}x");

        (concurrent / sequential).ShouldBeGreaterThan(3, $"concurrency 1 = {sequential:F1}/s, 8 = {concurrent:F1}/s");
    }

    public void Dispose()
    {
        foreach (var directory in _sessionDirectories.Where(Directory.Exists)) Directory.Delete(directory, recursive: true);
    }

    private async Task<double> DeploymentsPerSecondAsync(int concurrency)
    {
        await using var octopus = Octopus(deployments: 60);
        octopus.Latency = TimeSpan.FromMilliseconds(40);
        var clock = Stopwatch.StartNew();

        var cursor = await FetchUntilDoneAsync(octopus, concurrency, OctopusHistoryFetchPhase.Deployments);

        return cursor.Counts.Deployments / clock.Elapsed.TotalSeconds;
    }

    // Deployments with differing environments, channels, interruptions, events and artifacts, so order and content both matter.
    private static StubOctopusServer Octopus(int deployments, int releases = 0)
    {
        var octopus = StubOctopusServer.Start(ApiKey);

        foreach (var n in Enumerable.Range(1, releases)) octopus.Release("Projects-1", $"Releases-{n}", $"1.0.{n}");

        foreach (var n in Enumerable.Range(1, deployments))
        {
            octopus.Deployment("Projects-1", $"Deployments-{n}", "Releases-1", $"ServerTasks-{n}", n % 3 == 0 ? ["Machines-1", "Machines-2"] : null);
            octopus.Deployments["Projects-1"][^1]["EnvironmentId"] = $"Environments-{n % 5}";

            if (n % 4 == 0) octopus.Interruption($"ServerTasks-{n}", $"Interruptions-{n}", "ManualIntervention", isPending: false, result: "Proceed");
            if (n % 6 == 0) octopus.Artifact($"ServerTasks-{n}", $"Artifacts-{n}", $"report-{n}.txt");
        }

        return octopus;
    }

    private async Task<OctopusImportHistoryFetchDto> FetchUntilDoneAsync(StubOctopusServer octopus, int concurrency, OctopusHistoryFetchPhase phase = OctopusHistoryFetchPhase.Releases)
    {
        var options = new OctopusRestOptions { AllowLoopback = true, Concurrency = concurrency, ChunkDuration = TimeSpan.FromSeconds(60) };
        var fetcher = new OctopusRestHistoryFetcher(new OctopusRestClientFactory(null, options), new OctopusHistoryBundleWriter(), new OctopusImportHistoryDetailsStore(), options);
        var session = Path.Combine(Path.GetTempPath(), $"squid-it-rest-concurrency-{Guid.NewGuid():N}");
        var cursor = new OctopusImportHistoryFetchDto { ProjectIds = ["Projects-1"], Phase = phase, OctopusSpaceId = "Spaces-1" };

        _sessionDirectories.Add(session);

        while (!cursor.Done) await fetcher.FetchChunkAsync(new OctopusHistoryFetchContext { ServerUrl = octopus.BaseUri.ToString(), ApiKey = ApiKey, SessionDirectory = session, Cursor = cursor, PriorMappings = OctopusImportPriorMappings.Empty }).WaitAsync(TimeSpan.FromMinutes(2));

        return cursor;
    }

    private static string CursorJson(OctopusImportHistoryFetchDto cursor)
    {
        var node = JsonSerializer.SerializeToNode(cursor)!.AsObject();

        foreach (var name in new[] { "StartedAt", "CompletedAt", "ChunkId", "LeaseExpiresAt" }) node.Remove(name);

        return node.ToJsonString();
    }

    private static List<string> SessionFiles(string session)
        => Directory.EnumerateFiles(session, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(f => $"{Path.GetRelativePath(session, f)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}").ToList();
}
