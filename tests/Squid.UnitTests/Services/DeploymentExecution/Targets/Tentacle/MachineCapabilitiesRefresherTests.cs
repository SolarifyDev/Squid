using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Halibut.Diagnostics;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// The two ways a pod learns what an agent speaks now: adopting a newer persisted snapshot another pod (or an
/// upgrade/downgrade) wrote, and asking the agent itself. Neither may ever fail a dispatch, and neither may
/// replace a fresher local entry with an older persisted one.
/// </summary>
public class MachineCapabilitiesRefresherTests
{
    private const int MachineId = 7;
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly ServiceEndPoint Endpoint = new("poll://x/", "AABB", new HalibutTimeoutsAndLimits());

    private readonly InMemoryMachineRuntimeCapabilitiesCache _cache = new();
    private readonly Moq.Mock<IMachineRuntimeCapabilitiesPersistence> _persistence = new();
    private readonly Moq.Mock<IHalibutClientFactory> _factory = new();
    private readonly Moq.Mock<IAsyncCapabilitiesService> _caps = new();

    public MachineCapabilitiesRefresherTests()
    {
        _factory.Setup(f => f.CreateCapabilitiesClient(Endpoint)).Returns(_caps.Object);
    }

    private MachineCapabilitiesRefresher Refresher(HalibutSetting setting = null)
    {
        var store = new DirectStoreAccess(_persistence.Object);

        return new MachineCapabilitiesRefresher(_factory.Object, _cache, store, new MachineCapabilitiesRecorder(_cache, store), setting ?? new HalibutSetting());
    }

    private static MachineRuntimeCapabilities Snapshot(params string[] services) => new() { Os = "Linux", AgentVersion = "9.9.9", SupportedServices = services };

    private void CacheStored(DateTimeOffset at, params string[] services)
        => _cache.Store(MachineId, new Dictionary<string, string> { ["os"] = "Linux" }, "1.0.0", services, at);

    // ── SyncFromStoreAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task Sync_PersistedSnapshotIsNewer_ReplacesTheLocalEntry_WithItsOwnTimestamp()
    {
        CacheStored(T0, "IScriptService/v1", ScriptServiceCapabilities.V2);
        _persistence.Setup(p => p.LoadIfNewerAsync(MachineId, T0, default)).ReturnsAsync(new PersistedCapabilitiesSnapshot(Snapshot("IScriptService/v1"), T0.AddMinutes(1)));

        await Refresher().SyncFromStoreAsync(MachineId, default);

        var adopted = _cache.TryGet(MachineId);
        adopted.SupportsScriptServiceV2.ShouldBeFalse("another pod saw the agent after its downgrade");
        adopted.AgentVersion.ShouldBe("9.9.9");
        adopted.ObservedAt.ShouldBe(T0.AddMinutes(1));
    }

    [Fact]
    public async Task Sync_ComparesAgainstTheLocalEntrysTimestamp()
    {
        CacheStored(T0);

        await Refresher().SyncFromStoreAsync(MachineId, default);

        _persistence.Verify(p => p.LoadIfNewerAsync(MachineId, T0, default), Moq.Times.Once);
    }

    [Fact]
    public async Task Sync_NoLocalEntry_AsksForAnyPersistedSnapshot()
    {
        await Refresher().SyncFromStoreAsync(MachineId, default);

        _persistence.Verify(p => p.LoadIfNewerAsync(MachineId, null, default), Moq.Times.Once);
    }

    [Fact]
    public async Task Sync_NothingNewerPersisted_KeepsTheLocalEntry()
    {
        CacheStored(T0, ScriptServiceCapabilities.V2);
        _persistence.Setup(p => p.LoadIfNewerAsync(MachineId, T0, default)).ReturnsAsync((PersistedCapabilitiesSnapshot)null);

        await Refresher().SyncFromStoreAsync(MachineId, default);

        _cache.TryGet(MachineId).SupportsScriptServiceV2.ShouldBeTrue();
        _cache.TryGet(MachineId).ObservedAt.ShouldBe(T0);
    }

    [Fact]
    public async Task Sync_PersistedTombstone_EvictsTheLocalEntry_SoNoStaleV2IsAdvertised()
    {
        CacheStored(T0, ScriptServiceCapabilities.V2);
        _persistence.Setup(p => p.LoadIfNewerAsync(MachineId, T0, default)).ReturnsAsync(new PersistedCapabilitiesSnapshot(null, T0.AddMinutes(1)));

        await Refresher().SyncFromStoreAsync(MachineId, default);

        _cache.TryGet(MachineId).SupportsScriptServiceV2.ShouldBeFalse("an upgrade/downgrade elsewhere dropped the snapshot: V1 until the next health check");
        _cache.TryGet(MachineId).ObservedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Sync_PersistenceFails_SwallowsAndLeavesTheCacheAlone()
    {
        CacheStored(T0, ScriptServiceCapabilities.V2);
        _persistence.Setup(p => p.LoadIfNewerAsync(MachineId, T0, default)).ThrowsAsync(new InvalidOperationException("db down"));

        await Refresher().SyncFromStoreAsync(MachineId, default);

        _cache.TryGet(MachineId).SupportsScriptServiceV2.ShouldBeTrue("a failed comparison must behave as before the comparison existed");
    }

    [Fact]
    public async Task Sync_CancellationIsNotSwallowed()
    {
        _persistence.Setup(p => p.LoadIfNewerAsync(MachineId, null, Moq.It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => Refresher().SyncFromStoreAsync(MachineId, new CancellationToken(true)));
    }

    // ── RefreshFromAgentAsync ────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_AgentAnswers_StoresInCache_PersistsIt_AndReturnsWhatTheAgentSaid()
    {
        CacheStored(T0, ScriptServiceCapabilities.V2);
        _caps.Setup(c => c.GetCapabilitiesAsync(Moq.It.IsAny<CapabilitiesRequest>())).ReturnsAsync(new CapabilitiesResponse { AgentVersion = "1.0.0", SupportedServices = new List<string> { "IScriptService/v1" }, Metadata = new Dictionary<string, string> { ["os"] = "Linux" } });

        var fresh = await Refresher().RefreshFromAgentAsync(MachineId, Endpoint, default);

        fresh.SupportsScriptServiceV2.ShouldBeFalse();
        _cache.TryGet(MachineId).SupportsScriptServiceV2.ShouldBeFalse("this pod's cache now reflects the answer");
        _persistence.Verify(p => p.SaveAsync(MachineId, Moq.It.Is<MachineRuntimeCapabilities>(c => c.AgentVersion == "1.0.0" && !c.SupportsScriptServiceV2), default), Moq.Times.Once);
    }

    [Fact]
    public async Task Refresh_AgentAnswersWithNullMetadata_StillRecordsItsServices()
    {
        _caps.Setup(c => c.GetCapabilitiesAsync(Moq.It.IsAny<CapabilitiesRequest>())).ReturnsAsync(new CapabilitiesResponse { SupportedServices = new List<string> { ScriptServiceCapabilities.V2 }, Metadata = null });

        var fresh = await Refresher().RefreshFromAgentAsync(MachineId, Endpoint, default);

        fresh.SupportsScriptServiceV2.ShouldBeTrue();
    }

    [Fact]
    public async Task Refresh_PersistenceFails_StillReturnsTheFreshAnswer_AndTheCacheHasIt()
    {
        _persistence.Setup(p => p.SaveAsync(MachineId, Moq.It.IsAny<MachineRuntimeCapabilities>(), Moq.It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        _caps.Setup(c => c.GetCapabilitiesAsync(Moq.It.IsAny<CapabilitiesRequest>())).ReturnsAsync(new CapabilitiesResponse { SupportedServices = new List<string> { "IScriptService/v1" } });

        var fresh = await Refresher().RefreshFromAgentAsync(MachineId, Endpoint, default);

        fresh.ShouldNotBeNull();
        _cache.TryGet(MachineId).SupportedServices.ShouldBe(new[] { "IScriptService/v1" });
    }

    [Fact]
    public async Task Refresh_AgentFails_ReturnsNull_AndTouchesNeitherCacheNorStore()
    {
        CacheStored(T0, ScriptServiceCapabilities.V2);
        _caps.Setup(c => c.GetCapabilitiesAsync(Moq.It.IsAny<CapabilitiesRequest>())).ThrowsAsync(new HalibutClientException("down"));

        var fresh = await Refresher().RefreshFromAgentAsync(MachineId, Endpoint, default);

        fresh.ShouldBeNull();
        _cache.TryGet(MachineId).SupportsScriptServiceV2.ShouldBeTrue("nothing can be concluded from a failed probe");
        _persistence.Verify(p => p.SaveAsync(Moq.It.IsAny<int>(), Moq.It.IsAny<MachineRuntimeCapabilities>(), Moq.It.IsAny<CancellationToken>()), Moq.Times.Never);
    }

    [Fact]
    public async Task Refresh_AgentAnswersNull_ReturnsNull()
    {
        _caps.Setup(c => c.GetCapabilitiesAsync(Moq.It.IsAny<CapabilitiesRequest>())).ReturnsAsync((CapabilitiesResponse)null);

        (await Refresher().RefreshFromAgentAsync(MachineId, Endpoint, default)).ShouldBeNull();
    }

    [Fact]
    public async Task Refresh_AgentDoesNotAnswerInTime_ReturnsNull_WithinTheConfiguredBound()
    {
        _caps.Setup(c => c.GetCapabilitiesAsync(Moq.It.IsAny<CapabilitiesRequest>())).Returns(new TaskCompletionSource<CapabilitiesResponse>().Task);
        var setting = new HalibutSetting { Capabilities = new CapabilitiesSettings { RefreshTimeoutSeconds = 1 } };
        var started = DateTime.UtcNow;

        var fresh = await Refresher(setting).RefreshFromAgentAsync(MachineId, Endpoint, default);

        fresh.ShouldBeNull();
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(8), "the call is bounded by the setting, not by Halibut's queue timeout");
    }

    [Fact]
    public async Task Refresh_CallerCancels_Propagates()
    {
        _caps.Setup(c => c.GetCapabilitiesAsync(Moq.It.IsAny<CapabilitiesRequest>())).Returns(new TaskCompletionSource<CapabilitiesResponse>().Task);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => Refresher().RefreshFromAgentAsync(MachineId, Endpoint, cts.Token));
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    [Fact]
    public void CapabilitiesSettings_BindFromTheHalibutCapabilitiesSection_AndDefaultTo10Seconds()
    {
        new CapabilitiesSettings().RefreshTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        new HalibutSetting(new ConfigurationBuilder().Build()).Capabilities.RefreshTimeoutSeconds.ShouldBe(10);

        var bound = new HalibutSetting(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Halibut:Capabilities:RefreshTimeoutSeconds"] = "25" }).Build());

        bound.Capabilities.RefreshTimeout.ShouldBe(TimeSpan.FromSeconds(25));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void CapabilitiesSettings_InvalidValue_FallsBackToTheDefault(int configured)
        => new CapabilitiesSettings { RefreshTimeoutSeconds = configured }.RefreshTimeout.ShouldBe(TimeSpan.FromSeconds(CapabilitiesSettings.DefaultRefreshTimeoutSeconds));
}
