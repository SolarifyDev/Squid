using Microsoft.EntityFrameworkCore;
using Moq;
using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.IntegrationTests.Base;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;

namespace Squid.IntegrationTests.Services.DeploymentExecution;

/// <summary>
/// Capabilities shared between server pods through the machine row (real Postgres): a snapshot written by one pod
/// is adopted by another that holds an older entry, an invalidation (upgrade / downgrade) is a tombstone every pod
/// learns of, and a pod never replaces a fresher entry with an older persisted one. Each "pod" is its own cache +
/// refresher over the same database.
/// </summary>
public class MachineRuntimeCapabilitiesSharedStoreTests : TestBase
{
    public MachineRuntimeCapabilitiesSharedStoreTests() : base("MachineRuntimeCapabilitiesSharedStore", "squid_it_caps_shared_store")
    {
    }

    private static CapabilitiesResponse Agent(params string[] services) => new() { AgentVersion = "1.2.3", SupportedServices = services.ToList(), Metadata = new Dictionary<string, string> { ["os"] = "Linux" } };

    [Fact]
    public async Task SavedSnapshot_IsLoadedIfNewer_WithItsTimestamp_AndNotWhenNotNewer()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);

        await Run<IMachineRuntimeCapabilitiesPersistence>(p => p.SaveAsync(machineId, new MachineRuntimeCapabilities { Os = "Linux", SupportedServices = new[] { "IScriptService/v1", ScriptServiceCapabilities.V2 } }, default)).ConfigureAwait(false);

        var first = await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, null, default)).ConfigureAwait(false);

        first.ShouldNotBeNull();
        first.Capabilities.SupportsScriptServiceV2.ShouldBeTrue();
        first.Capabilities.ObservedAt.ShouldBe(first.UpdatedAt);

        (await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, first.UpdatedAt, default)).ConfigureAwait(false)).ShouldBeNull("equal is not newer");
        (await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, first.UpdatedAt.AddSeconds(1), default)).ConfigureAwait(false)).ShouldBeNull();
        (await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, first.UpdatedAt.AddSeconds(-1), default)).ConfigureAwait(false)).ShouldNotBeNull();
    }

    [Fact]
    public async Task MachineNeverProbed_HasNothingToLoad()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);

        (await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, null, default)).ConfigureAwait(false)).ShouldBeNull();
        (await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(int.MaxValue - 1, null, default)).ConfigureAwait(false)).ShouldBeNull("an unknown machine is not an error");
    }

    [Fact]
    public async Task Invalidate_LeavesATombstone_NullJsonWithAFreshTimestamp_AndHydrationStillSkipsIt()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);
        await Run<IMachineRuntimeCapabilitiesPersistence>(p => p.SaveAsync(machineId, new MachineRuntimeCapabilities { Os = "Linux" }, default)).ConfigureAwait(false);
        var saved = await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, null, default)).ConfigureAwait(false);

        await Run<IMachineRuntimeCapabilitiesPersistence>(p => p.InvalidateAsync(machineId, default)).ConfigureAwait(false);

        var tombstone = await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, saved.UpdatedAt, default)).ConfigureAwait(false);
        tombstone.ShouldNotBeNull("a pod holding the pre-invalidation entry must be able to see the drop");
        tombstone.Capabilities.ShouldBeNull();
        tombstone.UpdatedAt.ShouldBeGreaterThan(saved.UpdatedAt);

        var hydrated = await Run<IMachineRuntimeCapabilitiesPersistence, IReadOnlyList<(int MachineId, MachineRuntimeCapabilities Capabilities)>>(p => p.LoadAllAsync(default)).ConfigureAwait(false);
        hydrated.ShouldNotContain(r => r.MachineId == machineId, "startup hydration must not resurrect a dropped snapshot");
    }

    [Fact]
    public async Task HydratedRows_CarryTheirPersistedTimestamp_AsObservedAt()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);
        await Run<IMachineRuntimeCapabilitiesPersistence>(p => p.SaveAsync(machineId, new MachineRuntimeCapabilities { Os = "Linux" }, default)).ConfigureAwait(false);
        var saved = await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, null, default)).ConfigureAwait(false);

        var hydrated = await Run<IMachineRuntimeCapabilitiesPersistence, IReadOnlyList<(int MachineId, MachineRuntimeCapabilities Capabilities)>>(p => p.LoadAllAsync(default)).ConfigureAwait(false);

        hydrated.Single(r => r.MachineId == machineId).Capabilities.ObservedAt.ShouldBe(saved.UpdatedAt);
    }

    [Fact]
    public async Task APodThatMissedAnotherPodsDowngrade_AdoptsTheV1OnlySnapshot_OnItsNextSync()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);
        var podB = new InMemoryMachineRuntimeCapabilitiesCache();
        podB.Store(machineId, new Dictionary<string, string> { ["os"] = "Linux" }, "2.0.0", new[] { "IScriptService/v1", ScriptServiceCapabilities.V2 }, DateTimeOffset.UtcNow.AddMinutes(-10));

        // Pod A's health check, after the agent was downgraded, records the V1-only capabilities.
        await Run<IMachineCapabilitiesRecorder>(r => r.RecordAsync(machineId, Agent("IScriptService/v1"), default)).ConfigureAwait(false);

        await Run<IMachineCapabilitiesStoreAccess>(async a => await new MachineCapabilitiesRefresherHarness(podB, a).Refresher.SyncFromStoreAsync(machineId, default).ConfigureAwait(false)).ConfigureAwait(false);

        podB.TryGet(machineId).SupportsScriptServiceV2.ShouldBeFalse("pod B must stop advertising V2 without waiting for its own health check");
        podB.TryGet(machineId).AgentVersion.ShouldBe("1.2.3");
    }

    [Fact]
    public async Task APodThatMissedAnUpgradeInvalidation_EvictsItsEntry_OnItsNextSync()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);
        var podB = new InMemoryMachineRuntimeCapabilitiesCache();

        await Run<IMachineCapabilitiesRecorder>(r => r.RecordAsync(machineId, Agent("IScriptService/v1", ScriptServiceCapabilities.V2), default)).ConfigureAwait(false);
        await Run<IMachineCapabilitiesStoreAccess>(async a => await new MachineCapabilitiesRefresherHarness(podB, a).Refresher.SyncFromStoreAsync(machineId, default).ConfigureAwait(false)).ConfigureAwait(false);
        podB.TryGet(machineId).SupportsScriptServiceV2.ShouldBeTrue();

        await Run<IMachineRuntimeCapabilitiesPersistence>(p => p.InvalidateAsync(machineId, default)).ConfigureAwait(false);
        await Run<IMachineCapabilitiesStoreAccess>(async a => await new MachineCapabilitiesRefresherHarness(podB, a).Refresher.SyncFromStoreAsync(machineId, default).ConfigureAwait(false)).ConfigureAwait(false);

        podB.TryGet(machineId).SupportsScriptServiceV2.ShouldBeFalse("the invalidation cannot reach pod B's memory, but the tombstone does");
    }

    [Fact]
    public async Task APodNeverReplacesAFresherLocalEntry_WithAnOlderPersistedOne()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);
        await Run<IMachineCapabilitiesRecorder>(r => r.RecordAsync(machineId, Agent("IScriptService/v1", ScriptServiceCapabilities.V2), default)).ConfigureAwait(false);

        var pod = new InMemoryMachineRuntimeCapabilitiesCache();
        pod.Store(machineId, new Dictionary<string, string> { ["os"] = "Linux" }, "1.0.0", new[] { "IScriptService/v1" }, DateTimeOffset.UtcNow.AddMinutes(5));

        await Run<IMachineCapabilitiesStoreAccess>(async a => await new MachineCapabilitiesRefresherHarness(pod, a).Refresher.SyncFromStoreAsync(machineId, default).ConfigureAwait(false)).ConfigureAwait(false);

        pod.TryGet(machineId).SupportsScriptServiceV2.ShouldBeFalse("this pod's fresher probe (persist failed, say) must not be overwritten by an older shared copy");
    }

    [Fact]
    public async Task OnceAdopted_ASecondSyncReloadsNothing()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);
        await Run<IMachineCapabilitiesRecorder>(r => r.RecordAsync(machineId, Agent("IScriptService/v1"), default)).ConfigureAwait(false);
        var pod = new InMemoryMachineRuntimeCapabilitiesCache();

        await Run<IMachineCapabilitiesStoreAccess>(async a => await new MachineCapabilitiesRefresherHarness(pod, a).Refresher.SyncFromStoreAsync(machineId, default).ConfigureAwait(false)).ConfigureAwait(false);
        var adopted = pod.TryGet(machineId);

        var again = await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, adopted.ObservedAt, default)).ConfigureAwait(false);

        again.ShouldBeNull("the adopted entry carries the persisted timestamp, so the shared copy is not newer");
    }

    [Fact]
    public async Task RecordedCapabilities_CarryTheTimestampOfTheRowThatWasWritten_SoTheSharedCopyIsNotNewerThanTheCache()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        PersistedCapabilitiesSnapshot again = null;
        DateTimeOffset? rowStamp = null;

        await Run<IMachineCapabilitiesStoreAccess>(async store =>
        {
            await new MachineCapabilitiesRecorder(cache, store).RecordAsync(machineId, Agent("IScriptService/v1"), default).ConfigureAwait(false);

            again = await store.RunAsync(p => p.LoadIfNewerAsync(machineId, cache.TryGet(machineId).ObservedAt, default)).ConfigureAwait(false);
            rowStamp = (await store.RunAsync(p => p.LoadIfNewerAsync(machineId, null, default)).ConfigureAwait(false)).UpdatedAt;
        }).ConfigureAwait(false);

        cache.TryGet(machineId).ObservedAt.ShouldBe(rowStamp, "the cache must carry the timestamp the DB row was stamped with, not the local clock");
        again.ShouldBeNull("a cache stamped with the row's own timestamp must not see its own write as newer");
    }

    [Fact]
    public async Task SaveAsync_ReturnsExactlyTheStampOfTheRow_NotTheAppClock()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);
        await StampRowAsync(machineId, "now() + interval '1 hour'").ConfigureAwait(false);

        var returned = await Run<IMachineRuntimeCapabilitiesPersistence, DateTimeOffset>(p => p.SaveAsync(machineId, new MachineRuntimeCapabilities { Os = "Linux" }, default)).ConfigureAwait(false);
        var row = await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, null, default)).ConfigureAwait(false);

        returned.ShouldBe(row.UpdatedAt, "the recorder must stamp its cache with the value the row actually holds");
        returned.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(30), "a writer must order itself after a stamp from a pod whose clock runs ahead, which only holds if every writer shares the database's clock");
    }

    [Fact]
    public async Task Invalidate_AfterARecordStampedByAFastClockPod_IsStillStrictlyNewer()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);
        var fastPodStamp = await Run<IMachineRuntimeCapabilitiesPersistence, DateTimeOffset>(p => p.SaveAsync(machineId, new MachineRuntimeCapabilities { Os = "Linux" }, default)).ConfigureAwait(false);
        await StampRowAsync(machineId, "runtime_capabilities_updated_at + interval '30 seconds'").ConfigureAwait(false);
        var skewed = (await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, null, default)).ConfigureAwait(false)).UpdatedAt;
        skewed.ShouldBeGreaterThan(fastPodStamp);

        await Run<IMachineRuntimeCapabilitiesPersistence>(p => p.InvalidateAsync(machineId, default)).ConfigureAwait(false);

        var tombstone = await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, skewed, default)).ConfigureAwait(false);
        tombstone.ShouldNotBeNull("an upgrade tombstone written within the clock skew of a record must still be seen as newer by the pod that holds the record");
        tombstone.Capabilities.ShouldBeNull();
    }

    [Fact]
    public async Task TentacleHealthCheck_CachesTheStampOfTheRowItWrote_SoAnInvalidationFromAnotherPodIsSeen()
    {
        var machineId = await InsertMachineAsync().ConfigureAwait(false);
        await StampRowAsync(machineId, "now() + interval '1 hour'").ConfigureAwait(false);
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        var machine = new Machine { Id = machineId, Name = "m", Endpoint = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"CCDD"}""" };

        await Run<IMachineCapabilitiesStoreAccess>(async store =>
        {
            var strategy = new TentacleHealthCheckStrategy(CapabilitiesFactory(Agent("IScriptService/v1", ScriptServiceCapabilities.V2)), new MachineCapabilitiesRecorder(cache, store));

            (await strategy.CheckHealthAsync(machine, null, default).ConfigureAwait(false)).Healthy.ShouldBeTrue();
        }).ConfigureAwait(false);

        var row = await Run<IMachineRuntimeCapabilitiesPersistence, PersistedCapabilitiesSnapshot>(p => p.LoadIfNewerAsync(machineId, null, default)).ConfigureAwait(false);
        cache.TryGet(machineId).SupportsScriptServiceV2.ShouldBeTrue();
        cache.TryGet(machineId).ObservedAt.ShouldBe(row.UpdatedAt, "the health check must stamp its cache with the database's stamp for the row, not this pod's clock");

        await Run<IMachineRuntimeCapabilitiesPersistence>(p => p.InvalidateAsync(machineId, default)).ConfigureAwait(false);
        await Run<IMachineCapabilitiesStoreAccess>(async a => await new MachineCapabilitiesRefresherHarness(cache, a).Refresher.SyncFromStoreAsync(machineId, default).ConfigureAwait(false)).ConfigureAwait(false);

        cache.TryGet(machineId).SupportsScriptServiceV2.ShouldBeFalse("another pod's invalidation must evict the entry the health check cached");
    }

    [Fact]
    public async Task DependencyInjection_GivesTheTentacleHealthCheckTheRecorder_NotASilentNull()
    {
        // The recorder is an optional ctor param (= null): a registration that stopped resolving would turn the health
        // check back into a writer that never records, with every unit test still green.
        await Run<TentacleHealthCheckStrategy>(strategy =>
        {
            var recorder = typeof(TentacleHealthCheckStrategy).GetField("_capabilitiesRecorder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(strategy);

            recorder.ShouldBeOfType<MachineCapabilitiesRecorder>();

            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private static IHalibutClientFactory CapabilitiesFactory(CapabilitiesResponse response)
    {
        var caps = new Mock<IAsyncCapabilitiesService>();
        caps.Setup(c => c.GetCapabilitiesAsync(It.IsAny<CapabilitiesRequest>())).ReturnsAsync(response);
        var factory = new Mock<IHalibutClientFactory>();
        factory.Setup(f => f.CreateCapabilitiesClient(It.IsAny<Halibut.ServiceEndPoint>())).Returns(caps.Object);

        return factory.Object;
    }

    private Task StampRowAsync(int machineId, string sqlExpression) => Run<IRepository>(r => r.Database.ExecuteSqlRawAsync($"UPDATE machine SET runtime_capabilities_updated_at = {sqlExpression} WHERE id = {machineId}"));

    [Fact]
    public async Task ParallelSyncsAndRecords_InOneScope_NeverShareADbContext_EveryOneLands()
    {
        // A deployment dispatches to several machines in parallel inside ONE scope. Each capabilities read/write must
        // run on its own context, or EF rejects the overlap (and the swallowed failure would silently skip the adoption).
        var machineIds = new List<int>();
        for (var i = 0; i < 12; i++) machineIds.Add(await InsertMachineAsync().ConfigureAwait(false));

        var cache = new InMemoryMachineRuntimeCapabilitiesCache();

        await Run<IMachineCapabilitiesStoreAccess, IMachineCapabilitiesRecorder>(async (store, recorder) =>
        {
            await Task.WhenAll(machineIds.Select(id => recorder.RecordAsync(id, Agent("IScriptService/v1"), default))).ConfigureAwait(false);
            await Task.WhenAll(machineIds.Select(id => new MachineCapabilitiesRefresherHarness(cache, store).Refresher.SyncFromStoreAsync(id, default))).ConfigureAwait(false);
        }).ConfigureAwait(false);

        foreach (var id in machineIds)
            cache.TryGet(id).AgentVersion.ShouldBe("1.2.3", $"machine {id}'s persisted snapshot must have been adopted (and written) despite the parallel overlap");
    }

    private async Task<int> InsertMachineAsync()
    {
        var machineId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var machine = new Machine
            {
                Name = $"caps-{Guid.NewGuid():N}",
                IsDisabled = false,
                Roles = "[]",
                EnvironmentIds = "[]",
                Endpoint = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", SubscriptionId = $"sub-{Guid.NewGuid():N}", Thumbprint = "AA11" }),
                SpaceId = 1,
                Slug = $"caps-{Guid.NewGuid():N}"[..20]
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            machineId = machine.Id;
        }).ConfigureAwait(false);

        return machineId;
    }

    /// <summary>A refresher over another pod's cache and the shared (real) persistence; it never calls an agent.</summary>
    private sealed class MachineCapabilitiesRefresherHarness
    {
        public MachineCapabilitiesRefresherHarness(IMachineRuntimeCapabilitiesCache cache, IMachineCapabilitiesStoreAccess store)
        {
            Refresher = new MachineCapabilitiesRefresher(null, cache, store, new MachineCapabilitiesRecorder(cache, store), new Squid.Core.Settings.Halibut.HalibutSetting());
        }

        public MachineCapabilitiesRefresher Refresher { get; }
    }
}
