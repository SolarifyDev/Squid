using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Pins the dedup gate that makes durable persistence write a terminal outcome
/// exactly once instead of on every probe. The gate is the mechanism that lets
/// us keep the timeline cache in-memory (cheap per-probe writes) while still
/// surviving a restart (one durable write per concluded upgrade).
/// </summary>
public sealed class UpgradeTracePersistenceGateTests
{
    [Fact]
    public void AlreadyPersisted_ColdGate_ReturnsFalse()
    {
        var gate = new UpgradeTracePersistenceGate();

        gate.AlreadyPersisted(machineId: 1, signature: "SUCCESS@2026-06-01T10:00:00Z")
            .ShouldBeFalse("a machine never marked must report not-yet-persisted so its first terminal outcome is written.");
    }

    [Fact]
    public void MarkPersisted_ThenAlreadyPersisted_SameSignature_ReturnsTrue()
    {
        var gate = new UpgradeTracePersistenceGate();

        gate.MarkPersisted(1, "SUCCESS@2026-06-01T10:00:00Z");

        gate.AlreadyPersisted(1, "SUCCESS@2026-06-01T10:00:00Z")
            .ShouldBeTrue("the same terminal outcome the agent re-reports on every subsequent probe must NOT be re-persisted.");
    }

    [Fact]
    public void AlreadyPersisted_DifferentSignature_ReturnsFalse()
    {
        // A NEW upgrade concluded with a different outcome (or same status at a
        // later updatedAt). The signature changes → must persist again.
        var gate = new UpgradeTracePersistenceGate();

        gate.MarkPersisted(1, "FAILED@2026-06-01T10:00:00Z");

        gate.AlreadyPersisted(1, "SUCCESS@2026-06-02T09:00:00Z")
            .ShouldBeFalse("a new terminal outcome (different signature) must be persisted even after a prior one was.");
    }

    [Fact]
    public void MarkPersisted_OverwritesPriorSignatureForMachine()
    {
        // After a second upgrade, only the latest signature is the dedup key —
        // the previous one should no longer suppress writes (it can't recur
        // anyway, but pin the replace semantics).
        var gate = new UpgradeTracePersistenceGate();

        gate.MarkPersisted(1, "FAILED@t1");
        gate.MarkPersisted(1, "SUCCESS@t2");

        gate.AlreadyPersisted(1, "SUCCESS@t2").ShouldBeTrue();
        gate.AlreadyPersisted(1, "FAILED@t1").ShouldBeFalse(
            "only the most recently persisted signature dedups; the gate tracks the latest, not history.");
    }

    [Fact]
    public void Gate_PerMachineIsolation_NoCrossMachineSuppression()
    {
        var gate = new UpgradeTracePersistenceGate();

        gate.MarkPersisted(1, "SUCCESS@t");

        gate.AlreadyPersisted(2, "SUCCESS@t")
            .ShouldBeFalse("machine 2 marking is independent of machine 1 — a shared signature must not suppress a different machine's write.");
    }

    [Fact]
    public void MarkPersisted_NullSignature_DoesNotThrow_NormalisesToEmpty()
    {
        // Defensive: a snapshot with an all-null status would produce an odd
        // signature; null must not poison the map.
        var gate = new UpgradeTracePersistenceGate();

        Should.NotThrow(() => gate.MarkPersisted(1, null));

        gate.AlreadyPersisted(1, string.Empty).ShouldBeTrue();
    }
}
