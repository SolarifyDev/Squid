using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Xunit.Abstractions;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Measurement, not a gate (Category Performance): staging a large space's history in the database the way a fetch does (one staged
/// document per write, deployments with their task and details) and bundling it. Reports per-write latency, bundle time, stored against
/// logical bytes and the managed memory the bundle build needed, so a change to the store can be compared run against run.
/// </summary>
[Trait("Category", "Performance")]
public class OctopusImportSessionFileStagingPerformanceTests(ITestOutputHelper output) : TestBase("OctopusImportStagingPerformance", "squid_it_octopus_import_staging_performance")
{
    private const int Releases = 5000;
    private const int Deployments = 6500;

    [Fact]
    public async Task Staging_LargeSpace_ThenBundle_Measured()
    {
        var sessionId = await SeedSessionAsync();
        var directory = Path.Combine(Path.GetTempPath(), $"squid-it-staging-performance-{Guid.NewGuid():N}");

        try
        {
            var writes = await StageAsync(sessionId);
            var (bundle, elapsed, peak) = await BundleAsync(sessionId, directory);

            Report(writes, elapsed, peak, new FileInfo(bundle).Length, await StoredBytesAsync(sessionId));
            new FileInfo(bundle).Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<List<double>> StageAsync(Guid sessionId)
    {
        var latencies = new List<double>();

        await Run<IOctopusHistoryBundleWriter, IOctopusImportHistoryDetailsStore>(async (writer, details) =>
        {
            for (var n = 1; n <= Releases; n++) latencies.Add(await TimedAsync(() => writer.WriteJsonAsync(sessionId, OctopusResourceKind.Release, Release(n))));

            for (var n = 1; n <= Deployments; n++)
            {
                latencies.Add(await TimedAsync(() => details.WriteAsync(sessionId, $"ServerTasks-{n}", Details(n))));
                latencies.Add(await TimedAsync(() => writer.WriteJsonAsync(sessionId, OctopusResourceKind.ServerTask, Task(n))));
                latencies.Add(await TimedAsync(() => writer.WriteJsonAsync(sessionId, OctopusResourceKind.Deployment, Deployment(n))));
            }
        });

        return latencies;
    }

    private async Task<(string Bundle, TimeSpan Elapsed, long PeakManagedBytes)> BundleAsync(Guid sessionId, string directory)
    {
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var peak = before;
        var clock = Stopwatch.StartNew();
        using var sampling = new Timer(_ => peak = Math.Max(peak, GC.GetTotalMemory(false)), null, 0, 50);

        var bundle = await Run<IOctopusHistoryBundleWriter, string>(writer => writer.BuildBundleAsync(sessionId, directory, "inst-performance"));

        return (bundle, clock.Elapsed, peak - before);
    }

    private void Report(List<double> latencies, TimeSpan bundleTime, long peakBytes, long bundleBytes, (long Logical, long Stored) staged)
    {
        latencies.Sort();

        output.WriteLine($"writes: {latencies.Count}, p50 {latencies[latencies.Count / 2]:F2} ms, p95 {latencies[latencies.Count * 95 / 100]:F2} ms, total {latencies.Sum() / 1000:F1} s");
        output.WriteLine($"bundle: {bundleTime.TotalSeconds:F1} s, {bundleBytes / 1024 / 1024} MiB, peak managed growth {peakBytes / 1024 / 1024} MiB");
        output.WriteLine($"staged: logical {staged.Logical / 1024 / 1024} MiB, stored {staged.Stored / 1024 / 1024} MiB");
    }

    private static async Task<double> TimedAsync<T>(Func<Task<T>> write)
    {
        var clock = Stopwatch.StartNew();

        await write();

        return clock.Elapsed.TotalMilliseconds;
    }

    private Task<(long Logical, long Stored)> StoredBytesAsync(Guid sessionId)
        => Run<SquidDbContext, (long, long)>(async db =>
        {
            var logical = (await db.Database.SqlQuery<long>($"SELECT coalesce(sum(length), 0)::bigint AS \"Value\" FROM octopus_import_session_file WHERE session_id = {sessionId} AND path LIKE 'history-%'").ToListAsync()).Single();
            var stored = (await db.Database.SqlQuery<long>($"SELECT coalesce(sum(octet_length(content)), 0)::bigint AS \"Value\" FROM octopus_import_session_file_chunk WHERE session_id = {sessionId} AND path LIKE 'history-%'").ToListAsync()).Single();

            return (logical, stored);
        });

    private async Task<Guid> SeedSessionAsync()
    {
        var sessionId = Guid.NewGuid();

        await Run<SquidDbContext>(async db =>
        {
            db.Set<OctopusImportSession>().Add(new OctopusImportSession { SessionId = sessionId, DestinationSpaceId = 1, OwnerUserId = CurrentUsers.InternalUser.Id, State = OctopusImportSessionState.Uploaded.ToString(), SourceSummaryJson = "{}", TemporaryUploadPath = "/tmp/perf/octopus-history.zip", DataVersion = Guid.NewGuid().ToByteArray(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), LastStateChangedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

        return sessionId;
    }

    private static JsonObject Release(int n) => new() { ["Id"] = $"Releases-{n}", ["ProjectId"] = $"Projects-{n % 40}", ["ChannelId"] = $"Channels-{n % 40}", ["Version"] = $"1.{n / 100}.{n % 100}", ["ReleaseNotes"] = new string('r', 400), ["SelectedPackages"] = new JsonArray(new JsonObject { ["ActionName"] = "Deploy", ["Version"] = $"1.0.{n}", ["FeedId"] = "Feeds-1" }) };

    private static JsonObject Task(int n) => new() { ["Id"] = $"ServerTasks-{n}", ["Name"] = "Deploy", ["Description"] = $"Deploy release 1.0.{n} to Production", ["State"] = "Success", ["Arguments"] = new JsonObject { ["DeploymentId"] = $"Deployments-{n}" }, ["QueueTime"] = "2025-01-01T00:00:00Z", ["CompletedTime"] = "2025-01-01T00:05:00Z" };

    private static JsonObject Deployment(int n) => new() { ["Id"] = $"Deployments-{n}", ["ReleaseId"] = $"Releases-{n % Releases + 1}", ["EnvironmentId"] = $"Environments-{n % 6}", ["ProjectId"] = $"Projects-{n % 40}", ["ChannelId"] = $"Channels-{n % 40}", ["TaskId"] = $"ServerTasks-{n}", ["DeployedToMachineIds"] = new JsonArray("Machines-1", "Machines-2"), ["Name"] = $"Deploy to Production {n}" };

    // About the size of a real task's details after the importer's log tail: a few hundred log lines.
    private static OctopusTaskHistoryDetails Details(int n)
        => new() { ActivityLogs = [new OctopusActivityLogDto { Id = $"ServerTasks-{n}", Name = "Deploy", Status = "Success", LogElements = Enumerable.Range(1, 300).Select(line => new OctopusLogElementDto { Category = "Info", MessageText = $"[{line}] Step {line % 7}: applying manifest deployment-{n}-{line} to namespace production", OccurredAt = DateTimeOffset.UnixEpoch.AddSeconds(line) }).ToList() }] };
}
