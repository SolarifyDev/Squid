using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Pure-logic tests for the staleness predicate + lock-key pinning. The
/// Redis DEL side is covered by an integration test against a real Redis
/// instance (cheap to run; no server fixture required).
/// </summary>
public sealed class UpgradeDispatchLockReconcilerTests
{
    // ── ShouldClearLockForStatus: the decision matrix ────────────────────────

    [Fact]
    public void ShouldClearLockForStatus_NullPayload_ReturnsFalse()
    {
        UpgradeDispatchLockReconciler.ShouldClearLockForStatus(null).ShouldBeFalse();
    }

    [Fact]
    public void ShouldClearLockForStatus_SchemaVersion1_RefusesRegardlessOfAge()
    {
        // Schema v1 (1.4.x agent) doesn't emit startedAt; without that
        // field we can't reason about age. Conservative default = don't
        // clear. Operators on 1.4.x agents fall back on A1's 7-min TTL.
        var payload = new UpgradeStatusPayload
        {
            SchemaVersion = 1,
            Status = "IN_PROGRESS",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1) // even ancient
        };

        UpgradeDispatchLockReconciler.ShouldClearLockForStatus(payload).ShouldBeFalse();
    }

    [Theory]
    [InlineData("SUCCESS")]
    [InlineData("FAILED")]
    [InlineData("ROLLED_BACK")]
    [InlineData("ROLLBACK_NEEDED")]
    [InlineData("ROLLBACK_CRITICAL_FAILED")]
    [InlineData("SWAPPED")]
    [InlineData("")]
    [InlineData("SOMETHING_NEW_FROM_FUTURE_AGENT")]
    public void ShouldClearLockForStatus_NonInProgressStatus_ReturnsFalse(string status)
    {
        // Only IN_PROGRESS is a staleness candidate. Any other status means
        // the script reached a terminal state and released the lock already
        // (or will, once this health check returns and server clears via
        // normal path). Terminal statuses must never trigger the reconciler.
        var payload = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        UpgradeDispatchLockReconciler.ShouldClearLockForStatus(payload).ShouldBeFalse();
    }

    [Fact]
    public void ShouldClearLockForStatus_InProgressWithoutStartedAt_ReturnsFalse()
    {
        // Malformed v2 payload — shouldn't happen but defensive coding.
        // Missing StartedAt means we can't reason about age, so refuse.
        var payload = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "IN_PROGRESS",
            StartedAt = null
        };

        UpgradeDispatchLockReconciler.ShouldClearLockForStatus(payload).ShouldBeFalse();
    }

    [Fact]
    public void ShouldClearLockForStatus_InProgressJustStarted_ReturnsFalse()
    {
        // Healthy mid-flight upgrade. StartedAt recent — must NOT clear
        // the lock (would orphan a running dispatch, causing a duplicate
        // on next click).
        var payload = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "IN_PROGRESS",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        UpgradeDispatchLockReconciler.ShouldClearLockForStatus(payload).ShouldBeFalse();
    }

    [Fact]
    public void ShouldClearLockForStatus_InProgressAtThresholdBoundary_ReturnsFalse()
    {
        // Right AT the threshold — not yet stale. `>` is strict.
        var payload = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "IN_PROGRESS",
            StartedAt = DateTimeOffset.UtcNow - UpgradeDispatchLockReconciler.StalenessThreshold + TimeSpan.FromSeconds(5)
        };

        UpgradeDispatchLockReconciler.ShouldClearLockForStatus(payload).ShouldBeFalse();
    }

    [Fact]
    public void ShouldClearLockForStatus_InProgressPastThreshold_ReturnsTrue()
    {
        // The positive case — stale upgrade, clear the lock.
        var payload = new UpgradeStatusPayload
        {
            SchemaVersion = 2,
            Status = "IN_PROGRESS",
            StartedAt = DateTimeOffset.UtcNow - UpgradeDispatchLockReconciler.StalenessThreshold - TimeSpan.FromMinutes(1)
        };

        UpgradeDispatchLockReconciler.ShouldClearLockForStatus(payload).ShouldBeTrue();
    }

    // ── Threshold invariant: bounds the operator recovery window ────────────

    [Fact]
    public void StalenessThreshold_GreaterThanStrategyTimeout_ByAtLeast5Min()
    {
        // The threshold must give a slow-but-alive dispatch comfortable
        // headroom above the strategy's own timeout. If we clear too
        // eagerly, a legitimately-running 4-minute upgrade could get
        // its lock yanked mid-flight. 5 min buffer protects against
        // observation lag + GC pauses + health check scheduling jitter.
        var strategyTimeout = LinuxTentacleUpgradeStrategy.UpgradeScriptTimeout;
        var minimumThreshold = strategyTimeout + TimeSpan.FromMinutes(5);

        UpgradeDispatchLockReconciler.StalenessThreshold.ShouldBeGreaterThanOrEqualTo(minimumThreshold,
            $"StalenessThreshold ({UpgradeDispatchLockReconciler.StalenessThreshold}) must be >= " +
            $"strategyTimeout ({strategyTimeout}) + 5min buffer. Otherwise a legitimate slow upgrade " +
            "could have its Redis lock cleared mid-dispatch, causing a duplicate dispatch on next click.");
    }

    // ── Lock key format pinning — MUST match MachineUpgradeService ───────────

    [Fact]
    public void BuildLockKey_MatchesMachineUpgradeServiceFormat()
    {
        // Drift between the two lock-key formats would silently disable
        // staleness detection — MachineUpgradeService writes to one key,
        // reconciler reads a different one, reconciler "succeeds" but
        // the actual lock stays held forever. Pin via string equality
        // so a rename of either path forces an update to both.
        UpgradeDispatchLockReconciler.BuildLockKey(42).ShouldBe("squid:upgrade:machine:42");
    }

    [Theory]
    [InlineData(1, "squid:upgrade:machine:1")]
    [InlineData(42, "squid:upgrade:machine:42")]
    [InlineData(999999, "squid:upgrade:machine:999999")]
    public void BuildLockKey_EmbedsMachineId(int machineId, string expected)
    {
        UpgradeDispatchLockReconciler.BuildLockKey(machineId).ShouldBe(expected);
    }

    // ── UpgradeStatusPayload.TryParse resilience ────────────────────────────

    [Fact]
    public void TryParse_EmptyOrWhitespace_ReturnsNull()
    {
        UpgradeStatusPayload.TryParse(null).ShouldBeNull();
        UpgradeStatusPayload.TryParse("").ShouldBeNull();
        UpgradeStatusPayload.TryParse("   ").ShouldBeNull();
    }

    [Fact]
    public void TryParse_InvalidJson_ReturnsNull_NeverThrows()
    {
        // Agent could emit corrupted JSON (disk full mid-write, concurrent
        // reader reading partial file, etc). The parser must not propagate
        // — it degrades to "no status available" so the health check
        // completes successfully regardless.
        UpgradeStatusPayload.TryParse("not json").ShouldBeNull();
        UpgradeStatusPayload.TryParse("{").ShouldBeNull();
        UpgradeStatusPayload.TryParse("{\"status\":").ShouldBeNull();
    }

    [Fact]
    public void TryParse_SchemaV1Payload_DeserialisesWithDefaults()
    {
        // 1.4.x agent format — no schemaVersion field, no startedAt, no scriptPid.
        // Parser must accept (not crash) but the resulting payload must have
        // SchemaVersion == 1 (default) so the reconciler treats it conservatively.
        var raw = """
            {
              "targetVersion": "1.4.4",
              "installMethod": "apt",
              "status": "IN_PROGRESS",
              "updatedAt": "2026-04-22T02:57:04Z",
              "detail": "Selecting upgrade method"
            }
            """;

        var payload = UpgradeStatusPayload.TryParse(raw);

        payload.ShouldNotBeNull();
        payload.SchemaVersion.ShouldBe(1, customMessage: "no schemaVersion field → default to 1 → reconciler treats as legacy agent");
        payload.StartedAt.ShouldBeNull(customMessage: "v1 payloads lack startedAt — must deserialise as null, not crash");
        payload.ScriptPid.ShouldBeNull();
    }

    [Fact]
    public void TryParse_SchemaV2Payload_DeserialisesAllFields()
    {
        // 1.5.0+ format.
        var raw = """
            {
              "schemaVersion": 2,
              "targetVersion": "1.5.0",
              "installMethod": "apt",
              "status": "IN_PROGRESS",
              "startedAt": "2026-04-22T02:57:03Z",
              "updatedAt": "2026-04-22T02:57:04Z",
              "scriptPid": 21539,
              "detail": "Selecting upgrade method"
            }
            """;

        var payload = UpgradeStatusPayload.TryParse(raw);

        payload.ShouldNotBeNull();
        payload.SchemaVersion.ShouldBe(2);
        payload.TargetVersion.ShouldBe("1.5.0");
        payload.InstallMethod.ShouldBe("apt");
        payload.Status.ShouldBe("IN_PROGRESS");
        payload.StartedAt.ShouldNotBeNull();
        payload.StartedAt.Value.UtcDateTime.ShouldBe(new DateTime(2026, 4, 22, 2, 57, 3, DateTimeKind.Utc));
        payload.ScriptPid.ShouldBe(21539);
    }
}
