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

    // ── Phase B log cache (B4, 1.6.0) ────────────────────────────────────────

    [Fact]
    public void GetLog_ColdCache_ReturnsEmptyString_NeverNull()
    {
        // FE's "View full log" button renders the string directly — null
        // would crash a .length/.split call. Cold cache must be "".
        var store = new InMemoryUpgradeEventTimelineStore();

        store.GetLog(machineId: 42).ShouldBe(string.Empty);
    }

    [Fact]
    public void StoreLog_Then_GetLog_RoundTripsTheText()
    {
        var store = new InMemoryUpgradeEventTimelineStore();
        var logText = "=== In scope: continuing upgrade ===\nRestarting service...\n✓ Upgrade successful";

        store.StoreLog(machineId: 7, log: logText);

        store.GetLog(7).ShouldBe(logText);
    }

    [Fact]
    public void StoreLog_NullNormalisesToEmpty()
    {
        var store = new InMemoryUpgradeEventTimelineStore();

        store.StoreLog(1, null);

        store.GetLog(1).ShouldBe(string.Empty);
    }

    [Fact]
    public void StoreLog_OverwritesPreviousEntry()
    {
        // Same replace-not-append semantics as Store() for events — log
        // file gets truncated at Phase B start, so the cache always
        // reflects the current run.
        var store = new InMemoryUpgradeEventTimelineStore();

        store.StoreLog(1, "old run output");
        store.StoreLog(1, "NEW run output");

        store.GetLog(1).ShouldBe("NEW run output");
    }

    [Fact]
    public void Clear_RemovesEventsAndLogAndStatus()
    {
        // Cleanup helper must zero ALL entries — otherwise a partial
        // clear could leave stale log text or stale status visible while
        // events show fresh data, confusing operators.
        var store = new InMemoryUpgradeEventTimelineStore();
        store.Store(1, new[] { new UpgradeEvent { Kind = "x" } });
        store.StoreLog(1, "some log");
        store.StoreStatus(1, new UpgradeStatusPayload { Status = "SUCCESS" });

        store.Clear(1);

        store.Get(1).Count.ShouldBe(0);
        store.GetLog(1).ShouldBe(string.Empty);
        store.GetStatus(1).ShouldBeNull(
            customMessage: "Clear must drop the cached status payload too — partial clear leaves stale ExitCode visible to operators querying via GetUpgradeStatus");
    }

    // ========================================================================
    // UpgradeStatusPayload snapshot cache.
    //
    // The agent reports a structured status (with ExitCode after)
    // on every Capabilities probe; the cache holds the LATEST one so the
    // GetUpgradeStatus API can serve it to the FE without an extra RPC.
    // ========================================================================

    [Fact]
    public void GetStatus_ColdCache_ReturnsNull()
    {
        // Status is conceptually all-or-nothing (the agent either reported a
        // status or didn't). Null is the right zero state, not a default
        // payload — callers can distinguish "no upgrade ever ran" from
        // "upgrade ran but with all-empty fields".
        var store = new InMemoryUpgradeEventTimelineStore();

        store.GetStatus(machineId: 42).ShouldBeNull(
            customMessage: "cold cache must be null — distinguishes 'never reported' from 'reported with empty fields'");
    }

    [Fact]
    public void StoreStatus_Then_GetStatus_RoundTripsThePayload_IncludingExitCode()
    {
        // The whole point of 12.E.8: ExitCode survives the round-trip from
        // agent → CapabilitiesService → server-side parser → cache → API.
        // This test pins the cache layer.
        var store = new InMemoryUpgradeEventTimelineStore();
        var payload = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "FAILED",
            TargetVersion = "1.6.0",
            InstallMethod = "zip",
            ExitCode = 7,
            Detail = "SHA256 mismatch (expected ABC, got DEF)",
            StartedAt = DateTimeOffset.UtcNow
        };

        store.StoreStatus(machineId: 7, status: payload);

        var roundTripped = store.GetStatus(7);
        roundTripped.ShouldNotBeNull();
        roundTripped.Status.ShouldBe("FAILED");
        roundTripped.ExitCode.ShouldBe(7,
            customMessage: "ExitCode must round-trip through the cache; without this, the GetUpgradeStatus API would lose the exit-code information that's the whole reason this cache exists");
        roundTripped.Detail.ShouldContain("SHA256 mismatch");
    }

    [Fact]
    public void StoreStatus_NullPayload_DropsTheCachedEntry()
    {
        // Passing null is the explicit "clear status" sentinel. Some callers
        // (e.g., agent stops reporting because last-upgrade.json was deleted)
        // need to wipe the entry without a separate API method. Mirrors the
        // empty-storage protocol from .
        var store = new InMemoryUpgradeEventTimelineStore();
        store.StoreStatus(1, new UpgradeStatusPayload { Status = "SUCCESS" });
        store.GetStatus(1).ShouldNotBeNull("precondition: status was set");

        store.StoreStatus(1, status: null);

        store.GetStatus(1).ShouldBeNull(
            customMessage: "explicit null MUST drop the cached entry, not poison it with a null record. Without this, GetStatus would return null which the FE distinguishes from 'agent reported with all-empty fields' poorly.");
    }

    [Fact]
    public void StoreStatus_OverwritesPreviousEntry()
    {
        // Same replace-not-append semantic as Store / StoreLog. The agent
        // writes a fresh status on every transition; the cache always
        // reflects the most recent.
        var store = new InMemoryUpgradeEventTimelineStore();
        store.StoreStatus(1, new UpgradeStatusPayload { Status = "IN_PROGRESS", ExitCode = null });
        store.StoreStatus(1, new UpgradeStatusPayload { Status = "FAILED", ExitCode = 7 });

        var current = store.GetStatus(1);
        current.Status.ShouldBe("FAILED");
        current.ExitCode.ShouldBe(7);
    }

    [Fact]
    public void StoreStatus_PerMachineIsolation_NoCrossMachineLeakage()
    {
        // Two machines with different upgrade outcomes must not collide in
        // the cache. Mirrors the per-machine isolation pin already covered
        // for events — explicit pin so a future ConcurrentDictionary →
        // Dictionary refactor can't silently introduce sharing.
        var store = new InMemoryUpgradeEventTimelineStore();
        store.StoreStatus(1, new UpgradeStatusPayload { Status = "SUCCESS", ExitCode = 0 });
        store.StoreStatus(2, new UpgradeStatusPayload { Status = "FAILED", ExitCode = 7 });

        store.GetStatus(1).Status.ShouldBe("SUCCESS");
        store.GetStatus(1).ExitCode.ShouldBe(0);
        store.GetStatus(2).Status.ShouldBe("FAILED");
        store.GetStatus(2).ExitCode.ShouldBe(7);
    }
}
