using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Squid.E2ETests.HalibutRedisQueue;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Contracts.Tentacle;
using StackExchange.Redis;
using Xunit;

namespace Squid.E2ETests.HalibutRedisQueueSingleReplica;

/// <summary>
/// E2E, HIGH fidelity: ONE Squid server pod (a full container built through the real <c>SquidModule</c>, so the real heartbeat, wiring and queue), the real Postgres,
/// the real Redis behind a recording proxy, and a real Halibut polling agent. With no other replica the queue is entirely in memory: whatever the agent does (connects
/// late, drops and comes back, Redis dies) not one byte may reach Redis from the pod, and not one Redis key is created. The agent is an in-process runtime with a
/// recording script service, not the Tentacle binary.
/// </summary>
[Trait("Category", "E2E")]
public class HalibutRedisQueueSingleReplicaE2ETests : IClassFixture<OneReplicaFixture>
{
    private const string ScriptMarker = "echo E2E-SINGLE-REPLICA-MARKER-0b71";

    private readonly OneReplicaFixture _fixture;

    public HalibutRedisQueueSingleReplicaE2ETests(OneReplicaFixture fixture)
    {
        _fixture = fixture;
    }

    // The window a server restart leaves: the agent has not reconnected yet, so no poller of it has ever been seen by this pod. Before replicas were told apart such a
    // request was sent through Redis (and a Redis flush then failed it with a data-loss error instead of letting it wait in memory for the agent).
    [Fact]
    public async Task RequestIssuedBeforeTheAgentHasConnected_WaitsInMemoryForIt_AndRedisIsNeverTouched()
    {
        var pod = _fixture.Pod;

        await using var agent = TestAgent.PollingLater(pod, pod);

        var request = agent.CallFrom(pod, TimeSpan.FromSeconds(60)).StartScriptAsync(Script("s-before-connect"));

        await Task.Delay(TimeSpan.FromSeconds(3));
        request.IsCompleted.ShouldBeFalse("it waits in the pod's own queue for the agent");

        agent.Connect();

        (await request.WithinAsync(TimeSpan.FromSeconds(60), "the request issued before the agent connected")).State.ShouldBe(ProcessState.Complete);
        agent.Service.Started.ShouldContain("s-before-connect");

        _fixture.AssertRedisNeverTouched();
    }

    [Fact]
    public async Task AgentDropsAndReconnects_RequestsAroundTheGapAreServed_AndRedisIsNeverTouched()
    {
        var pod = _fixture.Pod;

        await using var agent = TestAgent.Polling(pod, pod);

        await agent.CallFrom(pod).StartScriptAsync(Script("s-warm")).WithinAsync(TimeSpan.FromSeconds(60), "the warm-up request");
        await agent.DisconnectAsync();

        var request = agent.CallFrom(pod, TimeSpan.FromSeconds(60)).StartScriptAsync(Script("s-during-gap"));

        await Task.Delay(TimeSpan.FromSeconds(2));

        agent.Connect();

        // The dequeue of the dropped connection may still take the request (Halibut's own behaviour, a transient failure); either way the pod did not use Redis.
        try
        {
            (await request.WithinAsync(TimeSpan.FromSeconds(60), "the request issued during the gap")).State.ShouldBe(ProcessState.Complete);
        }
        catch (Halibut.HalibutClientException)
        {
            (await agent.CallFrom(pod, TimeSpan.FromSeconds(60)).StartScriptAsync(Script("s-after-gap")).WithinAsync(TimeSpan.FromSeconds(60), "the retry")).State.ShouldBe(ProcessState.Complete);
        }

        _fixture.AssertRedisNeverTouched();
    }

    [Fact]
    public async Task RedisDown_ASingleReplicaIsUnaffected()
    {
        var pod = _fixture.Pod;

        await using var agent = TestAgent.Polling(pod, pod);

        _fixture.Proxy.Sever();

        try
        {
            var response = await agent.CallFrom(pod).StartScriptAsync(Script("s-redis-down")).WithinAsync(TimeSpan.FromSeconds(30), "a request with Redis down");

            response.State.ShouldBe(ProcessState.Complete);
            pod.Health.IsAvailable.ShouldBeTrue("nothing touched Redis, so nothing noticed it was down");
        }
        finally
        {
            _fixture.Proxy.Restore();
        }
    }

    private static StartScriptCommand Script(string ticket)
        => new(new ScriptTicket(ticket), ScriptMarker, ScriptIsolationLevel.NoIsolation, TimeSpan.FromSeconds(1), null, [], "e2e-task", TimeSpan.FromSeconds(1), Array.Empty<ScriptFile>());
}

/// <summary>The one pod, the recording proxy to Redis it is configured with, and a direct view of Redis to prove no key was created.</summary>
public sealed class OneReplicaFixture : IAsyncLifetime
{
    private readonly string _prefix = $"e2e{Guid.NewGuid():N}";
    private ConnectionMultiplexer _inspector;
    private string _realRedis;

    public CuttableTcpProxy Proxy { get; private set; }

    public RedisFailoverPod<HalibutRedisQueueSingleReplicaE2ETests> Pod { get; private set; }

    public async Task InitializeAsync()
    {
        var options = ConfigurationOptions.Parse(new ConfigurationBuilder().SetBasePath(Path.GetDirectoryName(typeof(OneReplicaFixture).Assembly.Location)!).AddJsonFile("appsettings.json").Build()["RedisCacheConnectionString"]!);
        var (host, port) = options.EndPoints[0] switch
        {
            System.Net.DnsEndPoint dns => (dns.Host, dns.Port),
            System.Net.IPEndPoint ip => (ip.Address.ToString(), ip.Port),
            var other => throw new InvalidOperationException($"Unsupported Redis endpoint {other}")
        };

        RequireRedis(host, port);

        _realRedis = $"{host}:{port}";
        Proxy = new CuttableTcpProxy(host, port);
        _inspector = await ConnectionMultiplexer.ConnectAsync($"{_realRedis},abortConnect=False");
        Pod = new RedisFailoverPod<HalibutRedisQueueSingleReplicaE2ETests>(Proxy.Address, _prefix);

        await Pod.InitializeAsync();
        await Pod.WaitUntilTrustLoadedAsync();

        // Two beats at the configured 1 s: the pod has had every chance to find another replica, and there is none.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
    }

    public async Task DisposeAsync()
    {
        if (Pod != null) await Pod.DisposeAsync();

        if (_inspector != null) await _inspector.DisposeAsync();

        Proxy?.Dispose();
    }

    /// <summary>The pod's own traffic to Redis never carried a queue key, a subscription, a ping or a request: it never even queued, probed or listened.</summary>
    public void AssertRedisNeverTouched()
    {
        var sent = Proxy.SentSince(0);

        sent.ShouldNotContain(_prefix, customMessage: "no queue key was ever addressed");
        sent.ShouldNotContain("SUBSCRIBE");
        sent.ShouldNotContain("PING");
        sent.ShouldNotContain("RPUSH");
        sent.ShouldNotContain("HSET");
        sent.ShouldNotContain("PUBLISH");
        _inspector.GetServer(_realRedis).Keys(pattern: $"{_prefix}*").ShouldBeEmpty("no key of the queue exists");
        Pod.Health.IsAvailable.ShouldBeTrue();
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
