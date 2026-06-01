using System;
using System.Collections.Generic;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Pins the pure hydrate step that runs at server startup: persisted snapshots
/// are replayed into the in-memory timeline store (so operators see the most
/// recent upgrade outcome immediately after a restart) AND the dedup gate is
/// primed (so the first post-restart probe doesn't re-write a snapshot already
/// on disk).
/// </summary>
public sealed class UpgradeTraceHydratorTests
{
    private static UpgradeTraceSnapshot Snapshot(string status, string updatedAt, params string[] eventKinds)
    {
        var events = new List<UpgradeEvent>();
        foreach (var k in eventKinds) events.Add(new UpgradeEvent { Kind = k });

        return new UpgradeTraceSnapshot
        {
            Status = new UpgradeStatusPayload { Status = status, UpdatedAt = DateTimeOffset.Parse(updatedAt) },
            Events = events,
            Log = $"log-for-{status}"
        };
    }

    [Fact]
    public void ApplyTo_PopulatesStore_StatusEventsAndLog_PerMachine()
    {
        var store = new InMemoryUpgradeEventTimelineStore();
        var gate = new UpgradeTracePersistenceGate();

        var rows = new List<(int, UpgradeTraceSnapshot)>
        {
            (10, Snapshot("SUCCESS", "2026-06-01T10:00:00Z", "start", "success")),
            (20, Snapshot("FAILED", "2026-06-01T11:00:00Z", "start", "fail"))
        };

        var hydrated = UpgradeTraceHydrator.ApplyTo(store, gate, rows);

        hydrated.ShouldBe(2);

        store.GetStatus(10).Status.ShouldBe("SUCCESS");
        store.Get(10).Count.ShouldBe(2);
        store.GetLog(10).ShouldBe("log-for-SUCCESS");

        store.GetStatus(20).Status.ShouldBe("FAILED");
        store.Get(20)[1].Kind.ShouldBe("fail");
    }

    [Fact]
    public void ApplyTo_PrimesGate_SoHydratedSnapshotIsNotRePersisted()
    {
        var store = new InMemoryUpgradeEventTimelineStore();
        var gate = new UpgradeTracePersistenceGate();
        var snapshot = Snapshot("SUCCESS", "2026-06-01T10:00:00Z", "success");

        UpgradeTraceHydrator.ApplyTo(store, gate, new List<(int, UpgradeTraceSnapshot)> { (10, snapshot) });

        gate.AlreadyPersisted(10, snapshot.Signature).ShouldBeTrue(
            customMessage: "hydrated snapshots must prime the gate; otherwise every machine re-persists its on-disk trace on the first post-restart probe (a write storm).");
    }

    [Fact]
    public void ApplyTo_SkipsNullSnapshots_DoesNotThrow()
    {
        var store = new InMemoryUpgradeEventTimelineStore();
        var gate = new UpgradeTracePersistenceGate();

        var rows = new List<(int, UpgradeTraceSnapshot)>
        {
            (10, Snapshot("SUCCESS", "2026-06-01T10:00:00Z", "success")),
            (20, null)
        };

        var hydrated = UpgradeTraceHydrator.ApplyTo(store, gate, rows);

        hydrated.ShouldBe(1);
        store.GetStatus(20).ShouldBeNull("a null snapshot row must be skipped, not stored as an empty entry.");
    }

    [Fact]
    public void ApplyTo_EmptyInput_ReturnsZero()
    {
        var store = new InMemoryUpgradeEventTimelineStore();
        var gate = new UpgradeTracePersistenceGate();

        UpgradeTraceHydrator.ApplyTo(store, gate, Array.Empty<(int, UpgradeTraceSnapshot)>()).ShouldBe(0);
    }
}
