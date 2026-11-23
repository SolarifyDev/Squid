using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Autofac;
using Halibut;
using Halibut.Diagnostics;
using Halibut.Queue.QueuedDataStreams;
using Halibut.Queue.Redis;
using Halibut.Queue.Redis.MessageStorage;
using Halibut.Queue.Redis.RedisDataLossDetection;
using Halibut.Queue.Redis.RedisHelpers;
using Halibut.ServiceModel;
using Npgsql;
using Shouldly;
using Squid.Core.Halibut.DataStreamStorage;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Settings.Halibut;
using Squid.IntegrationTests.Base;
using Squid.Message.Contracts.Tentacle;
using Xunit;

namespace Squid.E2ETests.HalibutStorage;

/// <summary>
/// E2E, HIGH fidelity for the store: three real <see cref="HalibutRuntime"/>s (the pod that takes the request, the pod that holds the agent's polling
/// connection, the agent), Halibut's real <see cref="RedisPendingRequestQueueFactory"/> over a real Redis, the real <see cref="DistributedDataStreamStore"/> of each
/// pod over a real Postgres, and Squid's real <c>IScriptService</c> contract carrying real <see cref="ScriptFile"/> DataStreams. The agent is an in-process runtime
/// with a recording script service, not the Tentacle binary. The production wiring of the queue comes later; this proves the store works inside the real queue.
/// Needs Redis at 127.0.0.1:6379; without it the test returns early (and says so) like the OS-guarded Tentacle E2Es.
/// </summary>
[Trait("Category", "HalibutRedisQueueE2E")]
public class HalibutRedisQueueDataStreamE2ETests : TestBase
{
    private const string RedisEndpoint = "127.0.0.1:6379";
    private const string Marker = "E2E-PLAINTEXT-MARKER-secret-value-77c1";

    public HalibutRedisQueueDataStreamE2ETests() : base("HalibutRedisQueueDataStreamE2E", "squid_e2e_halibut_redis_queue_data_stream")
    {
    }

    [Fact]
    public async Task ScriptFiles_StoredByThePodThatTookTheRequest_ReachTheAgent_ThroughThePodHoldingItsConnection()
    {
        if (!RedisAvailable())
        {
            Console.WriteLine($"SKIPPED: no Redis at {RedisEndpoint}; the real Redis queue cannot be exercised.");
            return;
        }

        using var ctx = new PodsContext(CurrentScope);
        var package = new Generated(20 * 1024 * 1024 + 3, 11);
        var variables = Encoding.UTF8.GetBytes($"{{\"Squid.Account.Password\":\"{Marker}\"}}");
        var sensitive = RandomNumberGenerator.GetBytes(4096);
        var files = new[] { ("package.nupkg", DataStreamFrom(package), (string)null), ("variables.json", DataStream.FromBytes(variables), null), ("sensitiveVariables.json", DataStream.FromBytes(sensitive), "the-agent-password"), ("empty.txt", DataStream.FromBytes([]), null) };

        var response = await ctx.Client.StartScriptAsync(Command(files));

        response.State.ShouldBe(ProcessState.Complete, "the agent answered through the queue and the pod holding its connection");
        ctx.Agent.Received["package.nupkg"].ShouldBe(new ReceivedFile(package.Length, package.Sha256, false), "20 MB moved byte-exact");
        ctx.Agent.Received["variables.json"].ShouldBe(new ReceivedFile(variables.Length, Convert.ToHexString(SHA256.HashData(variables)), true));
        ctx.Agent.Received["sensitiveVariables.json"].ShouldBe(new ReceivedFile(sensitive.Length, Convert.ToHexString(SHA256.HashData(sensitive)), false));
        ctx.Agent.Received["empty.txt"].Length.ShouldBe(0);

        ctx.TakingPodStore.Stored.ShouldBe(1, "the pod that took the request stored its streams");
        ctx.TakingPodStore.Rehydrated.ShouldBe(0);
        ctx.HoldingPodStore.Rehydrated.ShouldBe(1, "the pod holding the agent's connection rehydrated them");
        ctx.HoldingPodStore.Stored.ShouldBe(0);
        (await CountAsync("halibut_data_stream")).ShouldBe(0, "every stream was deleted when the holding pod finished reading it");
        (await CountAsync("halibut_data_stream_chunk")).ShouldBe(0);
    }

    private static ScriptStatusResponse Done(ScriptTicket ticket) => new(ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0);

    private static StartScriptCommand Command(IEnumerable<(string Name, DataStream Stream, string Password)> files)
        => new(new ScriptTicket("e2e-ticket"), "echo", ScriptIsolationLevel.NoIsolation, TimeSpan.FromSeconds(1), null, [], "e2e-task", TimeSpan.FromSeconds(1), files.Select(f => new ScriptFile(f.Name, f.Stream, f.Password)).ToArray());

    private static DataStream DataStreamFrom(Generated data) => new(data.Length, data.WriteToAsync);

    private async Task<long> CountAsync(string table)
    {
        string connectionString = null;

        await using (var scope = CurrentScope.BeginLifetimeScope()) connectionString = scope.Resolve<SquidDbContext>().Database.GetConnectionString();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);

        return (long)await command.ExecuteScalarAsync();
    }

    private static bool RedisAvailable()
    {
        try
        {
            using var client = new TcpClient();

            return client.ConnectAsync("127.0.0.1", 6379).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Test context ────────────────────────────────────────────────────────

    private sealed class PodsContext : IDisposable
    {
        private readonly List<IAsyncDisposable> _disposables = new();
        private readonly HalibutRuntime _takingPod;
        private readonly HalibutRuntime _holdingPod;
        private readonly HalibutRuntime _agent;

        public CountingStore TakingPodStore { get; }
        public CountingStore HoldingPodStore { get; }
        public RecordingScriptService Agent { get; } = new();
        public IAsyncScriptService Client { get; }

        public PodsContext(ILifetimeScope scope)
        {
            // Each pod is its own store instance over the shared database, as two replicas would be.
            TakingPodStore = new CountingStore(NewStore(scope));
            HoldingPodStore = new CountingStore(NewStore(scope));

            var prefix = Guid.NewGuid().ToString("N");
            var limits = HalibutTimeoutsAndLimits.RecommendedValues();
            var (takingCert, holdingCert, agentCert) = (NewCert("taking-pod"), NewCert("holding-pod"), NewCert("agent"));

            _takingPod = BuildPod(takingCert, TakingPodStore, prefix, limits);
            _holdingPod = BuildPod(holdingCert, HoldingPodStore, prefix, limits);
            _holdingPod.Trust(agentCert.Thumbprint);

            // Findings for the wiring: Halibut registers a contract's message types only when a client or service for it is created on that runtime. The pod that
            // dequeues a request has made no client of its own, so without this it fails to read the request ("Type specified in JSON ... was not resolved").
            _holdingPod.CreateAsyncClient<IScriptService, IAsyncScriptService>(new ServiceEndPoint(new Uri("poll://unused/"), "unused", limits));

            var holdingPort = _holdingPod.Listen();
            var services = new DelegateServiceFactory();

            services.Register<IScriptService, IScriptServiceAsync>(() => Agent);

            _agent = new HalibutRuntimeBuilder().WithServiceFactory(services).WithServerCertificate(agentCert).WithHalibutTimeoutsAndLimits(limits).Build();
            _agent.Poll(new Uri("poll://e2e-agent/"), new ServiceEndPoint(new Uri($"https://localhost:{holdingPort}/"), holdingCert.Thumbprint, limits), CancellationToken.None);

            Client = _takingPod.CreateAsyncClient<IScriptService, IAsyncScriptService>(new ServiceEndPoint(new Uri("poll://e2e-agent/"), agentCert.Thumbprint, limits));
        }

        private static DistributedDataStreamStore NewStore(ILifetimeScope scope)
            => new(scope.Resolve<IDataStreamPersistence>(), scope.Resolve<IDataStreamKeyProtector>(), new HalibutSetting());

        private HalibutRuntime BuildPod(X509Certificate2 cert, IStoreDataStreamsForDistributedQueues store, string prefix, HalibutTimeoutsAndLimits limits)
        {
            var logFactory = new LogFactory();
            var redis = new RedisFacade(RedisEndpoint, prefix, logFactory.ForPrefix("redis"));
            var watch = new WatchForRedisLosingAllItsData(redis, logFactory.ForPrefix("watch"));

            _disposables.Add(redis);
            _disposables.Add(watch);

            return new HalibutRuntimeBuilder()
                .WithServerCertificate(cert)
                .WithHalibutTimeoutsAndLimits(limits)
                .WithPendingRequestQueueFactory(serializer => new RedisPendingRequestQueueFactory(serializer, store, watch, new HalibutRedisTransport(redis), limits, logFactory))
                .Build();
        }

        private static X509Certificate2 NewCert(string name)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

            return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.Exportable);
        }

        public void Dispose()
        {
            _agent.Dispose();
            _holdingPod.Dispose();
            _takingPod.Dispose();

            foreach (var disposable in _disposables) disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class CountingStore(IStoreDataStreamsForDistributedQueues inner) : IStoreDataStreamsForDistributedQueues
    {
        public int Stored;
        public int Rehydrated;

        public async Task<byte[]> StoreDataStreams(IReadOnlyList<DataStream> dataStreams, bool useReceiver, CancellationToken cancellationToken)
        {
            // Only calls that carry streams: the response of this script service carries none, and Halibut still asks to store and rehydrate it.
            if (dataStreams.Count > 0) Interlocked.Increment(ref Stored);

            return await inner.StoreDataStreams(dataStreams, useReceiver, cancellationToken);
        }

        public async Task RehydrateDataStreams(byte[] dataStreamMetadata, List<IRehydrateDataStream> dataStreams, CancellationToken cancellationToken)
        {
            if (dataStreams.Count > 0) Interlocked.Increment(ref Rehydrated);

            await inner.RehydrateDataStreams(dataStreamMetadata, dataStreams, cancellationToken);
        }
    }

    private sealed record ReceivedFile(long Length, string Sha256, bool ContainedMarker);

    private sealed class RecordingScriptService : IScriptServiceAsync
    {
        public Dictionary<string, ReceivedFile> Received { get; } = new();

        public async Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command, CancellationToken ct)
        {
            foreach (var file in command.Files)
                Received[file.Name] = await ReadAsync(file.Contents, ct);

            return Done(command.ScriptTicket);
        }

        private static async Task<ReceivedFile> ReadAsync(DataStream stream, CancellationToken ct)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var length = 0L;
            var marker = Encoding.UTF8.GetBytes(Marker);
            var contained = false;

            await stream.Receiver().ReadAsync(async (source, token) =>
            {
                using var all = new MemoryStream();
                var buffer = new byte[81920];
                int read;

                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    length += read;

                    if (length <= 1024 * 1024) all.Write(buffer, 0, read);
                }

                contained = all.ToArray().AsSpan().IndexOf(marker) >= 0;
            }, ct);

            return new ReceivedFile(length, Convert.ToHexString(hash.GetHashAndReset()), contained);
        }

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request, CancellationToken ct) => Task.FromResult(Done(request.Ticket));

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command, CancellationToken ct) => Task.FromResult(Done(command.Ticket));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command, CancellationToken ct) => Task.FromResult(Done(command.Ticket));
    }

    /// <summary>Deterministic pseudo-random bytes produced on demand, so a 20 MB package is never held whole by the test either.</summary>
    private sealed class Generated
    {
        private readonly int _seed;

        public Generated(int length, int seed)
        {
            Length = length;
            _seed = seed;
            Sha256 = ComputeHash();
        }

        public int Length { get; }

        public string Sha256 { get; }

        public async Task WriteToAsync(Stream destination, CancellationToken ct)
        {
            await foreach (var block in Blocks(ct)) await destination.WriteAsync(block, ct);
        }

        private string ComputeHash()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            foreach (var block in BlocksSync()) hash.AppendData(block.Span);

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private async IAsyncEnumerable<ReadOnlyMemory<byte>> Blocks([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            foreach (var block in BlocksSync()) yield return block;
        }

        private IEnumerable<ReadOnlyMemory<byte>> BlocksSync()
        {
            var random = new Random(_seed);

            for (var remaining = Length; remaining > 0; remaining -= 65536)
            {
                var block = new byte[Math.Min(65536, remaining)];

                random.NextBytes(block);

                yield return block;
            }
        }
    }
}
