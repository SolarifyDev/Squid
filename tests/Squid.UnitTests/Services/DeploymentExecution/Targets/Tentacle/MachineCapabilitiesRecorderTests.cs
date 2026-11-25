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

    [Fact]
    public async Task Record_WhenPersistFails_FallsBackToLocalClock_AndStillStoresTheCache()
    {
        _persistence.Setup(p => p.SaveAsync(It.IsAny<int>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        var before = DateTimeOffset.UtcNow;

        var recorded = await Recorder().RecordAsync(MachineId, Agent(), CancellationToken.None);

        recorded.AgentVersion.ShouldBe("2.0.0");
        recorded.ObservedAt.ShouldNotBeNull();
        recorded.ObservedAt.Value.ShouldBeGreaterThanOrEqualTo(before);
    }
}
