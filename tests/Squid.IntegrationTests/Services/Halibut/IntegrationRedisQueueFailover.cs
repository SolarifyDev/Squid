using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Halibut;
using Halibut.Diagnostics;
using Halibut.Queue.MessageStreamWrapping;
using Squid.Core.Halibut.DataStreamStorage;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Halibut.RedisQueue.Encryption;
using Squid.Core.Halibut.Resilience;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Contracts.Tentacle;
using StackExchange.Redis;

namespace Squid.IntegrationTests.Services.HalibutRedisQueue;

/// <summary>
/// Integration tier against a real Redis (127.0.0.1:6379, the redis service of the CI workflows; the test FAILS, not skips, without one) and the real Postgres data
/// stream store. A real <see cref="HalibutRuntime"/> is built the way the module builds it (contract registry, sealing wrapper, failover factory, real Redis
/// backend). Proves what a fake cannot: what Redis really holds for an in-flight request, and how the breaker really behaves when the network to Redis is cut and restored.
/// </summary>
public class IntegrationRedisQueueFailover : TestBase
{
    private const string RedisAddress = "127.0.0.1:6379";
    private const string PasswordMarker = "REDIS-AT-REST-PASSWORD-MARKER-31c9";
    private const string ScriptMarker = "echo REDIS-AT-REST-SCRIPT-MARKER-77aa";

    public IntegrationRedisQueueFailover() : base("HalibutRedisQueueFailover", "squid_it_halibut_redis_queue_failover")
    {
    }

    [Fact]
    public async Task InFlightRequest_HoldsOnlyASealedEnvelopeInRedis_NoPlaintextAnywhere()
    {
        RequireRedis();

        await using var pod = NewPod(RedisAddress);

        var call = pod.Client.StartScriptAsync(Command());

        await WaitUntilAsync(() => pod.HoldsAQueuedRequest(), "the request to reach Redis");

        var dump = await pod.DumpRedisAsync();

        dump.Length.ShouldBeGreaterThan(0, "a scan of an empty Redis would prove nothing");
        dump.ShouldNotContain(PasswordMarker);
        dump.ShouldNotContain(ScriptMarker);
        dump.ShouldNotContain("IScriptService");
        dump.ShouldContain(QueueMessageEnvelope.Magic, customMessage: "the request is stored as a sealed queue message");

        await Should.ThrowAsync<Exception>(() => call);
    }

    [Fact]
    public async Task RedisCutAndRestored_BreakerOpensRequestWaitsLocallyAsTransient_ThenClosesWithoutARestart()
    {
        RequireRedis();

        using var proxy = new CuttableTcpProxy("127.0.0.1", 6379);
        await using var pod = NewPod(proxy.Address);

        // A request for an agent nobody polls goes through Redis while it is reachable.
        var first = pod.Client.StartScriptAsync(Command());

        await WaitUntilAsync(() => pod.HoldsAQueuedRequest(), "the request to reach Redis through the proxy");
        pod.Health.IsAvailable.ShouldBeTrue();

        proxy.Sever();

        await WaitUntilAsync(() => !pod.Health.IsAvailable, "the breaker to open after the network to Redis was cut", TimeSpan.FromSeconds(20));

        // With the breaker open the request is not sent to Redis: it waits in the local queue and ends as the same transient timeout as before Redis was involved.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Should.ThrowAsync<Exception>(() => pod.Client.StartScriptAsync(Command()));

        timer.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(6), "it waited for the agent (the 8 s polling-queue timeout) instead of failing at once");
        TransientFailureClassifier.IsTransient(ex).ShouldBeTrue($"{ex.GetType().Name}: {ex.Message}");

        proxy.Restore();

        await WaitUntilAsync(() => pod.Health.IsAvailable, "the breaker to close once Redis answers again", TimeSpan.FromSeconds(20));

        var afterRecovery = pod.Client.StartScriptAsync(Command());

        await WaitUntilAsync(() => pod.HoldsAQueuedRequest(), "a request to reach Redis again after recovery");

        _ = first;
        _ = afterRecovery;
    }

    private static StartScriptCommand Command()
        => new(new ScriptTicket(Guid.NewGuid().ToString("N")), ScriptMarker, ScriptIsolationLevel.NoIsolation, TimeSpan.FromSeconds(1), null, [], "it-task", TimeSpan.FromSeconds(1), [new ScriptFile("sensitiveVariables.json", DataStream.FromBytes(RandomNumberGenerator.GetBytes(256)), PasswordMarker)]);

    private static void RequireRedis()
    {
        try
        {
            using var client = new TcpClient();

            if (client.ConnectAsync("127.0.0.1", 6379).Wait(TimeSpan.FromSeconds(5)) && client.Connected) return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        throw new InvalidOperationException($"Redis is not reachable at {RedisAddress}. This test runs the real Redis queue and cannot run without it. Start one locally with: docker run -d --name squid-redis -p 6379:6379 redis:7");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? limit = null)
    {
        var until = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(15));

        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Timed out waiting for {what}");

            await Task.Delay(50);
        }
    }

    private Pod NewPod(string redisAddress) => new(CurrentScope, redisAddress);

    /// <summary>Another replica is alive: the only situation in which a request goes through Redis.</summary>
    private static ReplicaPresence PeerPresent()
    {
        var presence = new ReplicaPresence();

        presence.Set(true);

        return presence;
    }

    private sealed class Pod : IAsyncDisposable
    {
        private readonly HalibutRuntime _runtime;
        private readonly LazyRedisPendingRequestQueueFactory _redis;
        private readonly ConnectionMultiplexer _inspector;
        private readonly string _prefix = $"it{Guid.NewGuid():N}";

        public Pod(ILifetimeScope scope, string redisAddress)
        {
            var options = new RedisQueueOptions(_prefix, TimeSpan.FromSeconds(5), 2, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));
            var limits = HalibutTimeoutsAndLimits.RecommendedValues();
            var logFactory = new LogFactory();
            var keys = new QueueMessageKeys(scope.Resolve<IQueueMessageKeyProtector>(), options, TimeProvider.System);
            var store = scope.Resolve<DistributedDataStreamStore>();
            var slot = new QueueSerializerSlot();

            Health = new RedisQueueHealth(options);
            _redis = new LazyRedisPendingRequestQueueFactory(() => new RedisQueueBackend(new RedisQueueBackendInputs(redisAddress, options, Health, slot.Value, store, limits, logFactory)));
            _runtime = BuildRuntime(limits, logFactory, keys, options, slot);

            limits.PollingRequestQueueTimeout = TimeSpan.FromSeconds(8);
            Client = _runtime.CreateAsyncClient<IScriptService, IAsyncScriptService>(new ServiceEndPoint(new Uri("poll://it-agent/"), "thumbprint", limits));

            // The inspector reads Redis directly, never through the proxy that a test cuts.
            _inspector = ConnectionMultiplexer.Connect($"{RedisAddress},abortConnect=False");
        }

        public RedisQueueHealth Health { get; }

        public IAsyncScriptService Client { get; }

        /// <summary>A queued request is a hash key; the data-loss watch key and the pending list are there without one.</summary>
        public bool HoldsAQueuedRequest() => KeysInRedis().Any(key => key.ToString().Contains(":hash:"));

        public List<RedisKey> KeysInRedis() => _inspector.GetServer(RedisAddress).Keys(pattern: $"{_prefix}*").ToList();

        public async Task<string> DumpRedisAsync()
        {
            var db = _inspector.GetDatabase();
            var text = new StringBuilder();

            foreach (var key in KeysInRedis())
            {
                text.Append(key).Append('\n');

                switch (await db.KeyTypeAsync(key))
                {
                    case RedisType.Hash:
                        foreach (var entry in await db.HashGetAllAsync(key)) text.Append(entry.Name).Append('=').Append(Encoding.Latin1.GetString((byte[])entry.Value)).Append('\n');
                        break;
                    case RedisType.List:
                        foreach (var value in await db.ListRangeAsync(key)) text.Append(value).Append('\n');
                        break;
                    case RedisType.String:
                        text.Append(Encoding.Latin1.GetString((byte[])await db.StringGetAsync(key))).Append('\n');
                        break;
                }
            }

            return text.ToString();
        }

        private HalibutRuntime BuildRuntime(HalibutTimeoutsAndLimits limits, ILogFactory logFactory, QueueMessageKeys keys, RedisQueueOptions options, QueueSerializerSlot slot)
        {
            using var rsa = RSA.Create(2048);
            using var cert = new CertificateRequest("CN=it-pod", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

            return new HalibutRuntimeBuilder()
                .WithServerCertificate(X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty))
                .WithHalibutTimeoutsAndLimits(limits)
                .WithLogFactory(logFactory)
                .WithTypeRegistry(HalibutQueueWiring.BuildContractRegistry())
                .WithQueueMessageStreamWrappers(new MessageStreamWrappers([new QueueMessageStreamWrapper(keys)]))
                .WithPendingRequestQueueFactory(serializer =>
                {
                    slot.Value = serializer;

                    return new FailoverPendingRequestQueueFactory(limits, logFactory, _redis, new FailoverQueueContext(Health, options, TimeProvider.System, null, PeerPresent()));
                })
                .Build();
        }

        public async ValueTask DisposeAsync()
        {
            await _runtime.DisposeAsync();
            await _redis.DisposeAsync();

            foreach (var key in KeysInRedis()) await _inspector.GetDatabase().KeyDeleteAsync(key);

            await _inspector.DisposeAsync();
        }
    }
}
