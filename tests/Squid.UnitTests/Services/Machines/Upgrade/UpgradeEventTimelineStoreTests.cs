using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Behavioural pin for the in-memory upgrade event timeline cache.
/// Lifetime is process-local singleton; tests document the expected
/// thread-safety + replace semantics.
/// </summary>
public sealed class UpgradeEventTimelineStoreTests
{
    [Fact]
    public void Get_ColdCache_ReturnsEmptyList_NeverNull()
    {
        // Pre-condition for the FE: a never-stored machine must return
        // an empty list, not null. Frontend code that does `events.map(...)`
        // would crash if we returned null on a cold cache.
        var store = new InMemoryUpgradeEventTimelineStore();

        var events = store.Get(machineId: 42);

        events.ShouldNotBeNull();
        events.Count.ShouldBe(0);
    }

    [Fact]
    public void Store_Then_Get_RoundTripsTheList()
    {
        var store = new InMemoryUpgradeEventTimelineStore();
        var input = new[]
        {
            new UpgradeEvent { Phase = "A", Kind = "start", Message = "begin" },
            new UpgradeEvent { Phase = "B", Kind = "success", Message = "done" }
        };

        store.Store(machineId: 7, events: input);

        var roundTripped = store.Get(machineId: 7);
        roundTripped.Count.ShouldBe(2);
        roundTripped[0].Kind.ShouldBe("start");
        roundTripped[1].Kind.ShouldBe("success");
    }

    [Fact]
    public void Store_NullEvents_NormalisesToEmpty()
    {
        // Defensive: callers that mistakenly pass null shouldn't poison
        // the cache with a null entry that then NPE's on the next Get.
        var store = new InMemoryUpgradeEventTimelineStore();

        store.Store(machineId: 1, events: null);

        store.Get(1).Count.ShouldBe(0);
    }

    [Fact]
    public void Store_OverwritesPreviousEntry()
    {
        // Each health-check refresh REPLACES the timeline (not appends).
        // The agent's JSONL file IS the source of truth; the cache is
        // a hot-read projection, not an accumulator. Pin this so a
        // future "additive" refactor doesn't double-count events.
        var store = new InMemoryUpgradeEventTimelineStore();

        store.Store(1, new[] { new UpgradeEvent { Kind = "old-1" }, new UpgradeEvent { Kind = "old-2" } });
        store.Store(1, new[] { new UpgradeEvent { Kind = "new-1" } });

        var current = store.Get(1);
        current.Count.ShouldBe(1);
        current[0].Kind.ShouldBe("new-1");
    }

    [Fact]
    public void Clear_RemovesEntry()
    {
        var store = new InMemoryUpgradeEventTimelineStore();

        store.Store(1, new[] { new UpgradeEvent { Kind = "x" } });
        store.Clear(1);

        store.Get(1).Count.ShouldBe(0);
    }

    [Fact]
    public void Store_PerMachineIsolation_ReadsDontBleed()
    {
        var store = new InMemoryUpgradeEventTimelineStore();

        store.Store(1, new[] { new UpgradeEvent { Kind = "machine-1" } });
        store.Store(2, new[] { new UpgradeEvent { Kind = "machine-2" } });

        store.Get(1)[0].Kind.ShouldBe("machine-1");
        store.Get(2)[0].Kind.ShouldBe("machine-2");
    }
}
