using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class MachineCapabilitiesRecorderTests
{
    private const int MachineId = 77;

    private readonly Mock<IMachineRuntimeCapabilitiesPersistence> _persistence = new();
    private readonly InMemoryMachineRuntimeCapabilitiesCache _cache = new();

    private MachineCapabilitiesRecorder Recorder() => new(_cache, new DirectStoreAccess(_persistence.Object));

    private static CapabilitiesResponse Agent() => new() { AgentVersion = "2.0.0", SupportedServices = new List<string> { "IScriptService/v1" }, Metadata = new Dictionary<string, string> { ["os"] = "Linux" } };

    [Fact]
    public async Task Record_StampsTheCacheWithTheTimestampThatWasPersisted_NotTheLocalClock()
    {
        var persistedAt = DateTimeOffset.UtcNow.AddMinutes(-7);
        _persistence.Setup(p => p.SaveAsync(MachineId, It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>())).ReturnsAsync(persistedAt);

        var recorded = await Recorder().RecordAsync(MachineId, Agent(), CancellationToken.None);

        recorded.ObservedAt.ShouldBe(persistedAt, "the cache is compared against DB row stamps by LoadIfNewerAsync; a local-clock stamp lets clock skew hide newer rows");
        _cache.TryGet(MachineId).ObservedAt.ShouldBe(persistedAt);
    }

    // An unwritten probe must never outrank a row another pod stored (an invalidation after a downgrade): a local-clock stamp from a pod whose clock runs ahead would.
    [Fact]
    public async Task Record_WhenPersistFails_StillStoresTheCache_StampedSoAnyStoredRowOutranksIt()
    {
        _persistence.Setup(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));

        var recorded = await Recorder().RecordAsync(MachineId, Agent(), CancellationToken.None);

        recorded.AgentVersion.ShouldBe("2.0.0");
        recorded.ObservedAt.ShouldBe(DateTimeOffset.MinValue);
        _cache.TryGet(MachineId).ObservedAt.ShouldBe(DateTimeOffset.MinValue);
    }

    // The agent already answered: a cancellation that lands during the write must not throw that answer away, only its persistence.
    [Fact]
    public async Task Record_WhenCancelledDuringThePersist_StillCachesTheAnswer_StampedSoAnyStoredRowOutranksIt_AndPropagatesTheCancellation()
    {
        using var cts = new CancellationTokenSource();
        _persistence.Setup(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>())).Returns(() => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });

        await Should.ThrowAsync<OperationCanceledException>(() => Recorder().RecordAsync(MachineId, Agent(), cts.Token));

        _cache.TryGet(MachineId).AgentVersion.ShouldBe("2.0.0");
        _cache.TryGet(MachineId).ObservedAt.ShouldBe(DateTimeOffset.MinValue);
    }
}
