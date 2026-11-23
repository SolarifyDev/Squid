using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Autofac;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Squid.Core.Halibut;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Settings.SelfCert;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Contracts.Tentacle;
using StackExchange.Redis;
using Xunit;

namespace Squid.E2ETests.HalibutRedisQueue;

/// <summary>
/// E2E, HIGH fidelity: two Squid server pods, each a full container built through the real <c>SquidModule</c> (so the real <c>HalibutModule</c> wiring: failover queue,
/// sealing wrapper, contract registry, the shared Postgres data-stream store), the real Redis (the <c>RedisCacheConnectionString</c> of the E2E appsettings, reached
/// through a proxy the test can cut and that records every byte the pods send), and real Halibut polling agents. The agent is an in-process runtime with a recording
/// script service, not the Tentacle binary. Redis unreachable FAILS the tests.
/// </summary>
[Trait("Category", "E2E")]
public class HalibutRedisQueueFailoverE2ETests : IClassFixture<TwoPodsFixture>
{
    private const string PasswordMarker = "E2E-REDIS-PASSWORD-MARKER-5d02";
    private const string VariablesMarker = "E2E-REDIS-VARIABLES-MARKER-91be";
    private const string ScriptMarker = "echo E2E-REDIS-SCRIPT-MARKER-c47f";

    private readonly TwoPodsFixture _pods;

    public HalibutRedisQueueFailoverE2ETests(TwoPodsFixture pods)
    {
        _pods = pods;
    }

    [Fact]
    public async Task AgentPollsPodB_RequestIssuedOnPodA_IsServedThroughRedis()
    {
        await using var agent = _pods.StartAgent(_pods.B);

        var mark = _pods.Proxy.Mark();
        var response = await agent.CallFrom(_pods.A).StartScriptAsync(Script("a-1")).WithinAsync(TimeSpan.FromSeconds(60), "the cross-pod request");

        response.State.ShouldBe(ProcessState.Complete);
        agent.Service.Started.ShouldContain("a-1", "the agent polling pod B received the request issued on pod A");
        RedisCommandsSince(mark).ShouldNotBeEmpty("the request crossed pods through Redis");
    }

    [Fact]
    public async Task AgentPollsPodA_RequestIssuedOnPodA_IsServedLocally_AndCreatesNoRedisKeys()
    {
        await using var agent = _pods.StartAgent(_pods.A);

        // The first request may race the agent's connection and legitimately go through Redis; once one has been answered the agent is attached to this pod.
        await agent.CallFrom(_pods.A).StartScriptAsync(Script("b-warm")).WithinAsync(TimeSpan.FromSeconds(60), "the warm-up request");

        var mark = _pods.Proxy.Mark();
        var response = await agent.CallFrom(_pods.A).StartScriptAsync(Script("b-1")).WithinAsync(TimeSpan.FromSeconds(30), "the local request");

        response.State.ShouldBe(ProcessState.Complete);
        agent.Service.Started.ShouldContain("b-1");

        var sent = _pods.Proxy.SentSince(mark);

        sent.ShouldNotContain("HSET", customMessage: "a request for an agent polling this pod is never put into Redis");
        sent.ShouldNotContain("HMSET");
        sent.ShouldNotContain("RPUSH");
        sent.ShouldNotContain("PUBLISH");
        _pods.RedisKeysWithPrefix().Where(k => k.Contains(":hash:") || k.Contains(":list:")).ShouldBeEmpty("no request key was created for it");
    }

    [Fact]
    public async Task RedisCut_LocalRequestsStillSucceed_CrossPodWaitsLocallyThenFailsTransient_AndRecoversWithoutARestart()
    {
        await using var agent = _pods.StartAgent(_pods.B);

        await agent.CallFrom(_pods.A).StartScriptAsync(Script("c-warm")).WithinAsync(TimeSpan.FromSeconds(60), "the warm-up request");

        var baseline = await TimeAsync(() => agent.CallFrom(_pods.B).StartScriptAsync(Script("c-before")));

        _pods.Proxy.Sever();

        try
        {
            var duringCut = await TimeAsync(() => agent.CallFrom(_pods.B).StartScriptAsync(Script("c-cut")));

            duringCut.ShouldBeLessThan(baseline + TimeSpan.FromSeconds(1), "Redis going away must not slow a request for an agent polling this pod");

            await WaitUntilAsync(() => !_pods.A.Health.IsAvailable, "pod A's breaker to open", TimeSpan.FromSeconds(30));

            // The request cannot cross pods, so it waits in this pod's own queue, exactly as it did before Redis was involved, and fails as the same transient
            // timeout when the agent (which polls the other pod) never collects it. It does not fail at once, and it does not crash.
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var failure = await Should.ThrowAsync<Exception>(() => agent.CallFrom(_pods.A, TimeSpan.FromSeconds(4)).StartScriptAsync(Script("c-cross-cut")));

            timer.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(3), "it waited for the agent in the local queue instead of failing at once");
            timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
            TransientFailureClassifier.IsTransient(failure).ShouldBeTrue($"{failure.GetType().Name}: {failure.Message} must pause the deployment, not fail it");
            agent.Service.Started.ShouldNotContain("c-cross-cut");

            (await TimeAsync(() => agent.CallFrom(_pods.B).StartScriptAsync(Script("c-cut-open")))).ShouldBeLessThan(baseline + TimeSpan.FromSeconds(1));
        }
        finally
        {
            _pods.Proxy.Restore();
        }

        await WaitUntilAsync(() => _pods.A.Health.IsAvailable && _pods.B.Health.IsAvailable, "both breakers to close after Redis returned", TimeSpan.FromSeconds(30));

        var recovered = await RetryUntilAsync(() => agent.CallFrom(_pods.A).StartScriptAsync(Script("c-after")), TimeSpan.FromSeconds(90));

        recovered.State.ShouldBe(ProcessState.Complete, "cross-pod requests work again with no restart of anything");
        agent.Service.Started.ShouldContain("c-after");
    }

    // Redis down, the other pod alive and an agent that restarts: before Redis was involved the in-memory queue held the request until the agent reconnected.
    // It must still, however long the agent was away compared with the liveness window and however long Redis has been down.
    [Fact]
    public async Task RedisCut_AgentGoneAndBackAfterTwentySeconds_RequestIssuedWhileGoneSucceedsOnReconnect()
    {
        await using var agent = _pods.StartAgent(_pods.A);

        await agent.CallFrom(_pods.A).StartScriptAsync(Script("f-warm")).WithinAsync(TimeSpan.FromSeconds(60), "the warm-up request");

        _pods.Proxy.Sever();

        try
        {
            await WaitUntilAsync(() => !_pods.A.Health.IsAvailable, "pod A's breaker to open", TimeSpan.FromSeconds(30));
            await agent.DisconnectAsync();

            // Halibut's polling handler keeps the dequeue of a vanished agent outstanding for its 30 s polling wait, and a request taken by it fails as it always has.
            // Wait that out (and the liveness window after it), so that no poller is live and the request would need Redis if it could.
            await Task.Delay(TimeSpan.FromSeconds(37));

            var request = agent.CallFrom(_pods.A, TimeSpan.FromSeconds(120)).StartScriptAsync(Script("f-while-away"));

            await Task.Delay(TimeSpan.FromSeconds(20));
            request.IsCompleted.ShouldBeFalse($"the request waits in the local queue for the agent ({request.Status} {request.Exception?.GetBaseException().Message} started={string.Join(",", agent.Service.Started)})");

            agent.Connect();

            (await request.WithinAsync(TimeSpan.FromSeconds(60), "the request issued while the agent was away")).State.ShouldBe(ProcessState.Complete);
            agent.Service.Started.ShouldContain("f-while-away");
        }
        finally
        {
            _pods.Proxy.Restore();

            await WaitUntilAsync(() => _pods.A.Health.IsAvailable && _pods.B.Health.IsAvailable, "both breakers to close after Redis returned", TimeSpan.FromSeconds(30));
        }
    }

    // The agent restarts on a node served by the other pod. The old pod's polling handler keeps the dead connection's dequeue outstanding for Halibut's 30 s wait, and
    // the liveness window follows it, so for a while the old pod still counts the agent as attached. (A request issued during the first 30 s is taken by that dead
    // dequeue and fails with a connection reset: Halibut's own behaviour, the same on a single pod, and a transient failure the deployment pauses on.) One issued after
    // the dequeue ended but inside the window is queued locally, and must not be stranded there until the pickup timeout.
    [Fact]
    public async Task AgentMovesToAnotherPod_RequestIssuedOnTheOldPodWhileItStillCountsTheAgent_FollowsItThroughRedis()
    {
        await using var agent = _pods.StartAgent(_pods.A);

        await agent.CallFrom(_pods.A).StartScriptAsync(Script("g-warm")).WithinAsync(TimeSpan.FromSeconds(60), "the warm-up request");
        await agent.DisconnectAsync();

        agent.MoveTo(_pods.B);

        await Task.Delay(TimeSpan.FromSeconds(32));

        var response = await agent.CallFrom(_pods.A, TimeSpan.FromSeconds(120)).StartScriptAsync(Script("g-moved")).WithinAsync(TimeSpan.FromSeconds(100), "the request issued while the agent moved");

        response.State.ShouldBe(ProcessState.Complete);
        agent.Service.Started.ShouldContain("g-moved");
    }

    [Fact]
    public async Task SensitiveFileAndPackage_CrossPodsByteExact_AndNothingPlaintextReachesRedis()
    {
        await using var agent = _pods.StartAgent(_pods.B);

        var package = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 5);
        var variables = Encoding.UTF8.GetBytes($"{{\"Squid.Account.Password\":\"{VariablesMarker}\"}}");
        var mark = _pods.Proxy.Mark();

        var command = ScriptWith("d-1", new ScriptFile("package.nupkg", DataStream.FromBytes(package), null), new ScriptFile("variables.json", DataStream.FromBytes(variables), null), new ScriptFile("sensitiveVariables.json", DataStream.FromBytes(RandomNumberGenerator.GetBytes(512)), PasswordMarker));
        var response = await agent.CallFrom(_pods.A).StartScriptAsync(command).WithinAsync(TimeSpan.FromSeconds(90), "the cross-pod request with files");

        response.State.ShouldBe(ProcessState.Complete);

        var received = agent.Service.Files["d-1"];

        received["package.nupkg"].ShouldBe(new ReceivedFile(package.Length, Convert.ToHexString(SHA256.HashData(package)), null));
        received["variables.json"].ShouldBe(new ReceivedFile(variables.Length, Convert.ToHexString(SHA256.HashData(variables)), null));
        received["sensitiveVariables.json"].Password.ShouldBe(PasswordMarker, "the password reaches the agent intact");

        var sent = _pods.Proxy.SentSince(mark);

        sent.ShouldContain("SQQ1", customMessage: "what pods wrote to Redis is a sealed queue message");
        sent.ShouldNotContain(PasswordMarker, customMessage: "the file password is in the request message and must not be in Redis in the clear");
        sent.ShouldNotContain(ScriptMarker);
        sent.ShouldNotContain(VariablesMarker);
        sent.ShouldNotContain("IScriptService");
    }

    [Fact]
    public async Task ListeningTentacle_IsUnaffected_EvenWithRedisDown()
    {
        await using var agent = _pods.StartListeningAgent(_pods.A);

        _pods.Proxy.Sever();

        try
        {
            await WaitUntilAsync(() => !_pods.A.Health.IsAvailable, "pod A's breaker to open", TimeSpan.FromSeconds(30));

            var response = await agent.CallFrom(_pods.A).StartScriptAsync(Script("e-1")).WithinAsync(TimeSpan.FromSeconds(30), "the call to the listening Tentacle");

            response.State.ShouldBe(ProcessState.Complete);
            agent.Service.Started.ShouldContain("e-1");
        }
        finally
        {
            _pods.Proxy.Restore();

            await WaitUntilAsync(() => _pods.A.Health.IsAvailable && _pods.B.Health.IsAvailable, "both breakers to close after Redis returned", TimeSpan.FromSeconds(30));
        }
    }

    private static string[] RedisCommandsSince(string sent) => new[] { "HSET", "HMSET", "RPUSH", "PUBLISH" }.Where(sent.Contains).ToArray();

    private string[] RedisCommandsSince(int mark) => RedisCommandsSince(_pods.Proxy.SentSince(mark));

    private static StartScriptCommand Script(string ticket) => ScriptWith(ticket);

    private static StartScriptCommand ScriptWith(string ticket, params ScriptFile[] files)
        => new(new ScriptTicket(ticket), ScriptMarker, ScriptIsolationLevel.NoIsolation, TimeSpan.FromSeconds(1), null, [], "e2e-task", TimeSpan.FromSeconds(1), files);

    private static async Task<TimeSpan> TimeAsync(Func<Task<ScriptStatusResponse>> call)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var response = await call().WithinAsync(TimeSpan.FromSeconds(30), "a local request");

        response.State.ShouldBe(ProcessState.Complete);

        return timer.Elapsed;
    }

    private static async Task<T> RetryUntilAsync<T>(Func<Task<T>> call, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;

        while (true)
        {
            try
            {
                return await call();
            }
            catch (Exception) when (DateTime.UtcNow < until)
            {
                await Task.Delay(500);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;

        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Timed out waiting for {what}");

            await Task.Delay(100);
        }
    }
}

internal static class TaskLimits
{
    public static async Task<T> WithinAsync<T>(this Task<T> task, TimeSpan limit, string what)
    {
        try
        {
            return await task.WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{what} did not finish within {limit}");
        }
    }
}

public sealed record ReceivedFile(long Length, string Sha256, string Password);

/// <summary>What a <see cref="TestAgent"/> needs of a pod: its runtime to call through, and where and as whom it listens for polling agents.</summary>
public interface IFailoverPod
{
    HalibutRuntime Runtime { get; }

    string Thumbprint { get; }

    int PollingPort { get; }
}

/// <summary>One server pod: a container built through the real module with its own polling listener, certificate and the shared Postgres and Redis.</summary>
public sealed class RedisFailoverPod<TTest> : E2EFixtureBase<TTest>, IFailoverPod
{
    private readonly string _redis;
    private readonly string _prefix;

    public RedisFailoverPod(string redisAddress, string keyPrefix)
    {
        _redis = redisAddress;
        _prefix = keyPrefix;
        PollingPort = FreePort();
    }

    public int PollingPort { get; }

    public HalibutRuntime Runtime => LifetimeScope.Resolve<HalibutRuntime>();

    public IRedisQueueHealth Health => LifetimeScope.Resolve<IRedisQueueHealth>();

    public string Thumbprint
    {
        get
        {
            var cert = LifetimeScope.Resolve<SelfCertSetting>();

            using var loaded = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(cert.Base64), cert.Password);

            return loaded.Thumbprint;
        }
    }

    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        configuration["RedisCacheConnectionString"] = $"{_redis},abortConnect=False";
        configuration["Halibut:Polling:Enabled"] = "true";
        configuration["Halibut:Polling:Port"] = PollingPort.ToString();
        configuration["Halibut:RedisQueue:KeyPrefix"] = _prefix;
        configuration["Halibut:RedisQueue:LivenessWindowSeconds"] = "5";
        configuration["Halibut:RedisQueue:FailureThreshold"] = "2";
        configuration["Halibut:RedisQueue:OpenDurationSeconds"] = "2";
        configuration["Halibut:RedisQueue:ProbeIntervalSeconds"] = "1";
        configuration["Halibut:RedisQueue:ProbeTimeoutSeconds"] = "1";
        configuration["Halibut:RedisQueue:ReplicaHeartbeatSeconds"] = "1";

        // The trust list is refreshed from the database (which knows no test agent) on a timer; the test trusts its agents by hand, so the timer must not wipe them.
        configuration["Halibut:Trust:RefreshIntervalSeconds"] = "3600";
    }

    public IReplicaPresence Replicas => LifetimeScope.Resolve<IReplicaPresence>();

    /// <summary>The heartbeat has to have seen the other pod before a request may cross to it through Redis.</summary>
    public async Task WaitUntilAnotherReplicaIsLiveAsync()
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (!Replicas.OtherReplicaIsLive)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The pod never saw another live replica");

            await Task.Delay(50);
        }
    }

    public async Task WaitUntilTrustLoadedAsync()
    {
        var distributor = LifetimeScope.Resolve<IPollingTrustDistributor>();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (!distributor.InitialLoadCompleted)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The initial trust load did not complete");

            await Task.Delay(50);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);

        listener.Start();

        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>Both pods and the proxy to Redis that they share, started once for the class.</summary>
public sealed class TwoPodsFixture : IAsyncLifetime
{
    private readonly string _prefix = $"e2e{Guid.NewGuid():N}";
    private ConnectionMultiplexer _inspector;
    private string _realRedis;

    public CuttableTcpProxy Proxy { get; private set; }

    public RedisFailoverPod<HalibutRedisQueueFailoverE2ETests> A { get; private set; }

    public RedisFailoverPod<HalibutRedisQueueFailoverE2ETests> B { get; private set; }

    public async Task InitializeAsync()
    {
        var (host, port) = RealRedisEndpoint();

        RequireRedis(host, port);

        _realRedis = $"{host}:{port}";
        Proxy = new CuttableTcpProxy(host, port);
        _inspector = await ConnectionMultiplexer.ConnectAsync($"{_realRedis},abortConnect=False");

        A = new RedisFailoverPod<HalibutRedisQueueFailoverE2ETests>(Proxy.Address, _prefix);
        B = new RedisFailoverPod<HalibutRedisQueueFailoverE2ETests>(Proxy.Address, _prefix);

        await A.InitializeAsync();
        await B.InitializeAsync();
        await A.WaitUntilTrustLoadedAsync();
        await B.WaitUntilTrustLoadedAsync();
        await A.WaitUntilAnotherReplicaIsLiveAsync();
        await B.WaitUntilAnotherReplicaIsLiveAsync();
    }

    public async Task DisposeAsync()
    {
        // Pod A drops the shared database; pod B then only has its container to dispose.
        await A.DisposeAsync();
        await B.DisposeAsync();

        foreach (var key in RedisKeysWithPrefix()) await _inspector.GetDatabase().KeyDeleteAsync(key);

        await _inspector.DisposeAsync();
        Proxy.Dispose();
    }

    public string[] RedisKeysWithPrefix() => _inspector.GetServer(_realRedis).Keys(pattern: $"{_prefix}*").Select(k => k.ToString()).ToArray();

    public TestAgent StartAgent(IFailoverPod pollsThisPod) => TestAgent.Polling(pollsThisPod, A, B);

    public TestAgent StartListeningAgent(IFailoverPod calledBy) => TestAgent.Listening(calledBy, A, B);

    private static (string Host, int Port) RealRedisEndpoint()
    {
        var basePath = Path.GetDirectoryName(typeof(TwoPodsFixture).Assembly.Location)!;
        var configuration = new ConfigurationBuilder().SetBasePath(basePath).AddJsonFile("appsettings.json").Build();
        var options = ConfigurationOptions.Parse(configuration["RedisCacheConnectionString"]!);

        return options.EndPoints[0] switch
        {
            System.Net.DnsEndPoint dns => (dns.Host, dns.Port),
            System.Net.IPEndPoint ip => (ip.Address.ToString(), ip.Port),
            var other => throw new InvalidOperationException($"Unsupported Redis endpoint {other}")
        };
    }

    private static void RequireRedis(string host, int port)
    {
        try
        {
            using var client = new TcpClient();

            if (client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(5)) && client.Connected) return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }

        throw new InvalidOperationException($"Redis is not reachable at {host}:{port} (RedisCacheConnectionString in tests/Squid.E2ETests/appsettings.json). These tests run the real Redis queue and cannot run without it. Start one locally with: docker run -d --name squid-redis -p 6379:6379 redis:7");
    }
}

/// <summary>A real Halibut runtime standing in for a Tentacle: it polls (or listens) and records every script it is asked to start, with the files it received.</summary>
public sealed class TestAgent : IAsyncDisposable
{
    private readonly X509Certificate2 _cert;
    private readonly string _subscription;
    private readonly Uri _listenEndpoint;
    private IFailoverPod _polledPod;
    private HalibutRuntime _runtime;

    private TestAgent(X509Certificate2 cert, RecordingScriptService service, string subscription, Uri listenEndpoint, IFailoverPod polledPod, HalibutRuntime runtime)
    {
        _cert = cert;
        Service = service;
        _subscription = subscription;
        _listenEndpoint = listenEndpoint;
        _polledPod = polledPod;
        _runtime = runtime;
    }

    public RecordingScriptService Service { get; }

    public static TestAgent Polling(IFailoverPod pollsThisPod, params IFailoverPod[] allPods)
    {
        var (cert, service) = NewIdentity(allPods);
        var agent = new TestAgent(cert, service, $"e2e-{Guid.NewGuid():N}", null, pollsThisPod, null);

        agent.Connect();

        return agent;
    }

    /// <summary>An agent that is trusted but has not connected yet: <see cref="Connect"/> is called by the test.</summary>
    public static TestAgent PollingLater(IFailoverPod pollsThisPod, params IFailoverPod[] allPods)
    {
        var (cert, service) = NewIdentity(allPods);

        return new TestAgent(cert, service, $"e2e-{Guid.NewGuid():N}", null, pollsThisPod, null);
    }

    public static TestAgent Listening(IFailoverPod calledBy, params IFailoverPod[] allPods)
    {
        var (cert, service) = NewIdentity(allPods);
        var runtime = NewRuntime(cert, service);

        runtime.Trust(calledBy.Thumbprint);

        return new TestAgent(cert, service, null, new Uri($"https://localhost:{runtime.Listen()}/"), null, runtime);
    }

    /// <summary>The agent process going away: its polling connection drops and nothing polls until <see cref="Connect"/>.</summary>
    public async Task DisconnectAsync()
    {
        await _runtime.DisposeAsync();

        _runtime = null;
    }

    /// <summary>The agent restarting on another node: same identity and subscription, now polling a different pod. Only after <see cref="DisconnectAsync"/>.</summary>
    public void MoveTo(IFailoverPod pod)
    {
        _polledPod = pod;

        Connect();
    }

    /// <summary>The agent (re)starting: same identity and subscription, polling the same pod.</summary>
    public void Connect()
    {
        _runtime = NewRuntime(_cert, Service);

        _runtime.Poll(new Uri($"poll://{_subscription}/"), new ServiceEndPoint(new Uri($"https://localhost:{_polledPod.PollingPort}/"), _polledPod.Thumbprint, HalibutTimeoutsAndLimits.RecommendedValues()), CancellationToken.None);
    }

    /// <summary>A client of this agent as the given pod sees it. A request nobody collects within <paramref name="pickupTimeout"/> fails (the polling-queue timeout of the endpoint).</summary>
    public IAsyncScriptService CallFrom(IFailoverPod pod, TimeSpan? pickupTimeout = null)
    {
        var limits = HalibutTimeoutsAndLimits.RecommendedValues();

        if (pickupTimeout.HasValue) limits.PollingRequestQueueTimeout = pickupTimeout.Value;

        var endpoint = _subscription != null ? new ServiceEndPoint(new Uri($"poll://{_subscription}/"), _cert.Thumbprint, limits) : new ServiceEndPoint(_listenEndpoint, _cert.Thumbprint, limits);

        return pod.Runtime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint);
    }

    private static (X509Certificate2 Cert, RecordingScriptService Service) NewIdentity(IFailoverPod[] allPods)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=e2e-agent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.Exportable);

        // Every pod trusts the agent, as it does once trust is distributed through the shared database.
        foreach (var pod in allPods) pod.Runtime.Trust(cert.Thumbprint);

        return (cert, new RecordingScriptService());
    }

    private static HalibutRuntime NewRuntime(X509Certificate2 cert, RecordingScriptService service)
    {
        var services = new DelegateServiceFactory();

        services.Register<IScriptService, IScriptServiceAsync>(() => service);

        return new HalibutRuntimeBuilder().WithServiceFactory(services).WithServerCertificate(cert).WithHalibutTimeoutsAndLimits(HalibutTimeoutsAndLimits.RecommendedValues()).Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (_runtime != null) await _runtime.DisposeAsync();
    }
}

public sealed class RecordingScriptService : IScriptServiceAsync
{
    private readonly List<string> _started = new();

    public Dictionary<string, Dictionary<string, ReceivedFile>> Files { get; } = new();

    public IReadOnlyList<string> Started
    {
        get
        {
            lock (_started) return _started.ToList();
        }
    }

    public async Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command, CancellationToken ct)
    {
        var received = new Dictionary<string, ReceivedFile>();

        foreach (var file in command.Files)
            received[file.Name] = await ReadAsync(file, ct);

        lock (_started)
        {
            Files[command.ScriptTicket.TaskId] = received;
            _started.Add(command.ScriptTicket.TaskId);
        }

        return Done(command.ScriptTicket);
    }

    private static async Task<ReceivedFile> ReadAsync(ScriptFile file, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = 0L;

        await file.Contents.Receiver().ReadAsync(async (source, token) =>
        {
            var buffer = new byte[81920];
            int read;

            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                length += read;
            }
        }, ct);

        return new ReceivedFile(length, Convert.ToHexString(hash.GetHashAndReset()), file.EncryptionPassword);
    }

    private static ScriptStatusResponse Done(ScriptTicket ticket) => new(ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0);

    public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request, CancellationToken ct) => Task.FromResult(Done(request.Ticket));

    public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command, CancellationToken ct) => Task.FromResult(Done(command.Ticket));

    public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command, CancellationToken ct) => Task.FromResult(Done(command.Ticket));
}
