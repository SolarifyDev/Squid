using System.IO;
using System.Linq;
using Squid.Core.Settings.Setup;
using Squid.Core.Setup.Embedded;

namespace Squid.UnitTests.Setup;

/// <summary>Process-level contract of the built-in PostgreSQL / Redis, verified against a recording runner.</summary>
public sealed class EmbeddedServicesTests
{
    [Fact]
    public async Task Postgres_FreshCluster_AsRoot_InitialisesAndStartsAsPostgresUser()
    {
        using var dir = new TempDataDirectory();
        var runner = new RecordingRunner(exitCodeFor: r => r.Arguments.Contains("status") ? 3 : 0);
        var postgres = new EmbeddedPostgres(runner, new EmbeddedPostgresSettings { Password = "pw", Port = 5433 }, dir.Path, "/pg/bin", runAsServiceUser: true);

        await postgres.StartAsync(CancellationToken.None);

        runner.Calls.Select(Describe).ShouldBe(new[] { "chown", "runuser /pg/bin/initdb", "find", "chown", "runuser /pg/bin/pg_ctl" });

        var initdb = runner.Calls[1].Arguments;
        initdb.ShouldContain("--auth=scram-sha-256");
        initdb.ShouldContain(a => a.StartsWith("--pwfile="));

        var start = runner.Calls[4].Arguments;
        start.ShouldContain(a => a.Contains("listen_addresses=127.0.0.1") && a.Contains("port=5433"));
        start.Last().ShouldBe("start");

        Directory.GetFiles(dir.Path, ".pg-init-*").ShouldBeEmpty();
        File.Exists(Path.Combine(dir.Path, EmbeddedPostgres.InitializingMarkerName)).ShouldBeFalse();
    }

    [Fact]
    public async Task Postgres_InterruptedInitdb_DiscardsHalfBuiltClusterAndInitialisesAgain()
    {
        using var dir = new TempDataDirectory();
        var cluster = Path.Combine(dir.Path, EmbeddedPostgres.DataFolderName);
        Directory.CreateDirectory(cluster);
        File.WriteAllText(Path.Combine(cluster, "PG_VERSION"), "15");
        File.WriteAllText(Path.Combine(cluster, "half-written"), "x");
        File.WriteAllText(Path.Combine(dir.Path, EmbeddedPostgres.InitializingMarkerName), "interrupted");
        var runner = new RecordingRunner(exitCodeFor: _ => 0);

        EmbeddedPostgres.ReadClusterMajorVersion(dir.Path).ShouldBeNull();

        await new EmbeddedPostgres(runner, new EmbeddedPostgresSettings { Password = "pw" }, dir.Path, "/pg/bin", runAsServiceUser: false).StartAsync(CancellationToken.None);

        runner.Calls.Select(Describe).ShouldBe(new[] { "/pg/bin/initdb", "/pg/bin/pg_ctl" });
        File.Exists(Path.Combine(cluster, "half-written")).ShouldBeFalse();
        File.Exists(Path.Combine(dir.Path, EmbeddedPostgres.InitializingMarkerName)).ShouldBeFalse();
    }

    [Fact]
    public async Task Postgres_ClusterOwnedByAnotherUid_IsHandedBackBeforeStart()
    {
        using var dir = new TempDataDirectory();
        CreateCompleteCluster(dir, "15");
        var runner = new RecordingRunner(exitCodeFor: _ => 0, outputFor: r => r.FileName == "find" ? "/data/pgdata/base" : "");

        await new EmbeddedPostgres(runner, new EmbeddedPostgresSettings { Password = "pw" }, dir.Path, "/pg/bin", runAsServiceUser: true).StartAsync(CancellationToken.None);

        runner.Calls.Select(Describe).ShouldBe(new[] { "find", "chown", "chown", "runuser /pg/bin/pg_ctl" });
        runner.Calls[1].Arguments.ShouldBe(new[] { "-R", "postgres:postgres", Path.Combine(dir.Path, EmbeddedPostgres.DataFolderName) });
    }

    [Fact]
    public void Postgres_ExistingClusterVersion_IsReadForBinarySelection()
    {
        using var dir = new TempDataDirectory();

        EmbeddedPostgres.ReadClusterMajorVersion(dir.Path).ShouldBeNull();

        CreateCompleteCluster(dir, "15\n");

        EmbeddedPostgres.ReadClusterMajorVersion(dir.Path).ShouldBe(15);
    }

    [Fact]
    public async Task Redis_StalePidFile_NeverSignalsAnotherProcess()
    {
        using var dir = new TempDataDirectory();
        var redis = new EmbeddedRedis(new RecordingRunner(exitCodeFor: _ => 0), new EmbeddedRedisSettings { Password = "pw" }, dir.Path, "/usr/bin/redis-server");
        Directory.CreateDirectory(redis.StorageDirectory);
        File.WriteAllText(redis.PidFile, Environment.ProcessId.ToString());
        var runner = new RecordingRunner(exitCodeFor: _ => 0);

        await new EmbeddedRedis(runner, new EmbeddedRedisSettings { Password = "pw" }, dir.Path, "/usr/bin/redis-server").StopAsync(CancellationToken.None);

        runner.Calls.ShouldBeEmpty();
    }

    private static void CreateCompleteCluster(TempDataDirectory dir, string version)
    {
        var cluster = Path.Combine(dir.Path, EmbeddedPostgres.DataFolderName);
        Directory.CreateDirectory(cluster);
        File.WriteAllText(Path.Combine(cluster, "PG_VERSION"), version);
    }

    [Fact]
    public async Task Postgres_ExistingCluster_NotRoot_SkipsInitdbAndRunsToolsDirectly()
    {
        using var dir = new TempDataDirectory();
        Directory.CreateDirectory(Path.Combine(dir.Path, EmbeddedPostgres.DataFolderName));
        File.WriteAllText(Path.Combine(dir.Path, EmbeddedPostgres.DataFolderName, "PG_VERSION"), "16");
        var runner = new RecordingRunner(exitCodeFor: _ => 0);

        await new EmbeddedPostgres(runner, new EmbeddedPostgresSettings { Password = "pw" }, dir.Path, "/pg/bin", runAsServiceUser: false).StartAsync(CancellationToken.None);

        runner.Calls.Select(Describe).ShouldBe(new[] { "/pg/bin/pg_ctl" });
    }

    [Fact]
    public async Task Postgres_FailedTool_SurfacesItsOutput()
    {
        using var dir = new TempDataDirectory();
        var runner = new RecordingRunner(exitCodeFor: _ => 1, output: "initdb: directory not empty");
        var postgres = new EmbeddedPostgres(runner, new EmbeddedPostgresSettings { Password = "pw" }, dir.Path, "/pg/bin", runAsServiceUser: false);

        var ex = await Should.ThrowAsync<EmbeddedServiceException>(() => postgres.StartAsync(CancellationToken.None));

        ex.Message.ShouldContain("initdb: directory not empty");
    }

    [Fact]
    public void Postgres_ConnectionString_IsLoopbackOnly()
    {
        EmbeddedPostgres.BuildConnectionString(new EmbeddedPostgresSettings { Port = 5544, Password = "pw" }).ShouldBe("Host=127.0.0.1;Port=5544;Database=squid;Username=squid;Password=pw");
    }

    [Fact]
    public void Redis_ConnectionString_IsLoopbackAndPasswordProtected()
    {
        EmbeddedRedis.BuildConnectionString(new EmbeddedRedisSettings { Port = 6390, Password = "pw" }).ShouldStartWith("127.0.0.1:6390,password=pw,");
    }

    private static string Describe(ProcessRunRequest request) => request.FileName == "runuser" ? $"runuser {request.Arguments[3]}" : request.FileName;

    private sealed class RecordingRunner(Func<ProcessRunRequest, int> exitCodeFor, string output = "", Func<ProcessRunRequest, string> outputFor = null) : IProcessRunner
    {
        public List<ProcessRunRequest> Calls { get; } = new();

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct)
        {
            Calls.Add(request);

            return Task.FromResult(new ProcessRunResult(exitCodeFor(request), outputFor?.Invoke(request) ?? output));
        }
    }
}
