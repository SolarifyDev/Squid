using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// The periodic Tentacle health check is the busiest writer of a machine's capabilities. It must stamp the cache with the
/// timestamp the database row was stamped with (like every other writer), never the pod's own clock, or a pod whose clock
/// runs ahead keeps advertising ScriptService V2 to an agent another pod just invalidated.
/// </summary>
public class TentacleHealthCheckCapabilitiesStampTests
{
    private const int MachineId = 41;

    private readonly Mock<IHalibutClientFactory> _clientFactory = new();
    private readonly Mock<IMachineRuntimeCapabilitiesPersistence> _persistence = new();
    private readonly InMemoryMachineRuntimeCapabilitiesCache _cache = new();

    public TentacleHealthCheckCapabilitiesStampTests()
    {
        var caps = new Mock<IAsyncCapabilitiesService>();
        caps.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).ReturnsAsync(new CapabilitiesResponse { AgentVersion = "2.0.0", SupportedServices = new List<string> { "IScriptService/v1", "IScriptService/v2" }, Metadata = new Dictionary<string, string> { ["os"] = "Linux" } });
        _clientFactory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<ServiceEndPoint>())).Returns(caps.Object);
    }

    private static Machine Machine() => new() { Id = MachineId, Name = "m", Endpoint = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""" };

    private TentacleHealthCheckStrategy Strategy() => TentacleStrategy.Build(_clientFactory.Object, _cache, _persistence.Object);

    [Fact]
    public async Task HealthCheck_StampsTheCacheWithTheStampTheRowWasPersistedWith_NotThePodClock()
    {
        var dbStamp = DateTimeOffset.UtcNow.AddMinutes(-9);
        _persistence.Setup(p => p.SaveAsync(MachineId, It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>())).ReturnsAsync(dbStamp);

        var result = await Strategy().CheckHealthAsync(Machine(), null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        _cache.TryGet(MachineId).ObservedAt.ShouldBe(dbStamp);
    }

    [Fact]
    public async Task HealthCheck_WhenPersistingFails_IsStillHealthy_AndCachesWithTheLocalClock()
    {
        _persistence.Setup(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        var before = DateTimeOffset.UtcNow;

        var result = await Strategy().CheckHealthAsync(Machine(), null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        _cache.TryGet(MachineId).SupportsScriptServiceV2.ShouldBeTrue();
        _cache.TryGet(MachineId).ObservedAt.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public async Task HealthCheck_WhenPersistingTimesOutWithoutCallerCancellation_IsStillHealthy()
    {
        _persistence.Setup(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TaskCanceledException("db command timeout"));

        var result = await Strategy().CheckHealthAsync(Machine(), null, CancellationToken.None);

        result.Healthy.ShouldBeTrue("a database timeout is a persistence failure, not a failed health check");
    }

    [Fact]
    public async Task HealthCheck_WhenTheCallerCancels_StillPropagatesTheCancellation()
    {
        using var cts = new CancellationTokenSource();
        _persistence.Setup(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>())).Returns(() => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });

        await Should.ThrowAsync<OperationCanceledException>(() => Strategy().CheckHealthAsync(Machine(), null, cts.Token));
    }
}

/// <summary>Builds the strategy the way the tests need it; one place to change if its wiring changes.</summary>
internal static class TentacleStrategy
{
    public static TentacleHealthCheckStrategy Build(IHalibutClientFactory factory, IMachineRuntimeCapabilitiesCache cache, IMachineRuntimeCapabilitiesPersistence persistence) => new(factory, new MachineCapabilitiesRecorder(cache, new DirectStoreAccess(persistence)));
}
