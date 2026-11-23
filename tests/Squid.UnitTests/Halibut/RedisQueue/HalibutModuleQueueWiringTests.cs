using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Autofac;
using Halibut;
using Halibut.Diagnostics;
using Halibut.Transport.Protocol;
using Squid.Core.Halibut;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Halibut.RedisQueue.Encryption;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.UnitTests.Support;

namespace Squid.UnitTests.Halibut.RedisQueue;

/// <summary>
/// The seam tests: the queue pieces are each correct on their own, and none of that matters if the runtime Squid really builds does not use them. They build the
/// real <see cref="HalibutModule"/> and look at what the real <see cref="HalibutRuntime"/> does, so deleting the wiring call turns them red.
/// </summary>
[Collection(GlobalStateSerialisedCollection.Name)]
public sealed class HalibutModuleQueueWiringTests
{
    private static readonly BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void ThePollingQueueTheRuntimeCreates_IsTheFailoverQueue()
    {
        using var container = BuildContainer();
        var runtime = container.Resolve<HalibutRuntime>();

        var queue = typeof(HalibutRuntime).GetMethod("GetQueue", NonPublic)!.Invoke(runtime, [new Uri("poll://seam-agent/")]);

        queue.ShouldBeOfType<FailoverPendingRequestQueue>("a polling agent's queue must be local first with Redis behind it, not Halibut's plain in-memory queue");
    }

    // A guard nobody proved is called guards nothing: the queue must read the replica presence the heartbeat writes, or a multi-replica install would never use Redis
    // (and a single replica could never be told apart from one).
    [Fact]
    public async Task TheQueue_ReadsTheReplicaPresenceTheHeartbeatPublishes()
    {
        using var container = BuildContainer();
        var runtime = container.Resolve<HalibutRuntime>();
        var queue = typeof(HalibutRuntime).GetMethod("GetQueue", NonPublic)!.Invoke(runtime, [new Uri("poll://seam-agent/")]);
        var read = typeof(FailoverPendingRequestQueue).GetField("_replicas", NonPublic)!.GetValue(queue);
        var presence = container.Resolve<IReplicaPresence>();

        read.ShouldBeSameAs(presence);

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!presence.OtherReplicaIsLive && DateTime.UtcNow < until) await Task.Delay(20);

        presence.OtherReplicaIsLive.ShouldBeTrue("the heartbeat is started with the container and publishes what the store reports");
    }

    // The pod that takes a request off Redis has usually never created a client for its contract; without the message types registered up front it cannot read the request.
    [Fact]
    public void ARuntimeThatNeverCreatedAClient_AlreadyKnowsEveryAgentContract()
    {
        using var container = BuildContainer();
        var runtime = container.Resolve<HalibutRuntime>();
        var registry = (ITypeRegistry)typeof(HalibutRuntime).GetField("typeRegistry", NonPublic)!.GetValue(runtime)!;

        registry.IsInAllowedTypes(typeof(StartScriptCommand)).ShouldBeTrue();
        registry.IsInAllowedTypes(typeof(ScriptStatusResponse)).ShouldBeTrue();
        registry.IsInAllowedTypes(typeof(CapabilitiesResponse)).ShouldBeTrue();
        registry.IsInAllowedTypes(typeof(StartScriptCommandV2)).ShouldBeTrue("a V2 request issued on another pod must be readable here");
        registry.IsInAllowedTypes(typeof(ScriptStatusResponseV2)).ShouldBeTrue();
    }

    [Fact]
    public async Task ACallToAListeningTentacle_NeverCreatesAQueue()
    {
        using var container = BuildContainer();
        var runtime = container.Resolve<HalibutRuntime>();
        var limits = HalibutTimeoutsAndLimits.RecommendedValues();

        limits.RetryCountLimit = 1;
        limits.RetryListeningSleepInterval = TimeSpan.FromMilliseconds(50);
        limits.TcpClientConnectTimeout = TimeSpan.FromSeconds(1);

        var client = runtime.CreateAsyncClient<IScriptService, IAsyncScriptService>(new ServiceEndPoint(new Uri($"https://127.0.0.1:{ClosedPort()}/"), "thumbprint", limits));

        await Should.ThrowAsync<HalibutClientException>(() => client.GetStatusAsync(new ScriptStatusRequest(new ScriptTicket("t"), 0)));

        ((IDictionary)typeof(HalibutRuntime).GetField("queues", NonPublic)!.GetValue(runtime)!).Count.ShouldBe(0, "a listening Tentacle is called over a direct connection: no queue, so no Redis");
    }

    [Fact]
    public void EveryPod_SealsQueuedMessages()
    {
        using var container = BuildContainer();

        container.Resolve<IQueueMessageKeys>().ShouldBeOfType<QueueMessageKeys>();
        container.Resolve<IQueueMessageKeyProtector>().ShouldBeOfType<QueueMessageKeyProtector>();
    }

    private static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);

        listener.Start();

        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        listener.Stop();

        return port;
    }

    private static IContainer BuildContainer()
    {
        using var rsa = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=queue-wiring", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        var builder = new ContainerBuilder();

        builder.RegisterInstance(new SelfCertSetting { Base64 = Convert.ToBase64String(cert.Export(X509ContentType.Pfx, string.Empty)), Password = string.Empty }).AsSelf().SingleInstance();
        builder.RegisterModule(new HalibutModule());
        builder.RegisterInstance<IReplicaStore>(new OtherReplicaStore()).SingleInstance();

        return builder.Build();
    }

    /// <summary>The database side of the heartbeat (the real one is picked up by the application's dependency scan): always reports one other replica.</summary>
    private sealed class OtherReplicaStore : IReplicaStore
    {
        public Task<int> BeatAsync(Guid replicaId, TimeSpan liveWindow, CancellationToken ct) => Task.FromResult(1);

        public Task LeaveAsync(Guid replicaId, CancellationToken ct) => Task.CompletedTask;
    }
}
