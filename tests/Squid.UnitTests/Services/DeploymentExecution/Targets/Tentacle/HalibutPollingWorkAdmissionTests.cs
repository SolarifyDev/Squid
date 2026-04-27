using System.Collections.Generic;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Xunit;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// P1-Phase9b.1 (audit item B.2) — pin the bounded polling-work admission
/// gate that protects server memory against unbounded backlog when an agent
/// goes offline.
///
/// <para><b>The failure scenario this guards</b>: 1000 deployments queued
/// for an offline polling Tentacle. Each Hangfire worker picks up a deploy,
/// dispatches via Halibut, the script command sits in Halibut's in-memory
/// pending-request queue waiting for the agent to poll. Agent never polls
/// (network partition, crashed process). Halibut's queue grows unbounded;
/// server RAM grows; OOM-killer terminates Squid. Pre-Phase-9b.1 there was
/// no upper bound — every queued deploy succeeded into Halibut's queue.</para>
///
/// <para>Fix shape: per-machine in-flight counter held by the strategy. New
/// dispatches above the limit are rejected fast with a structured exception;
/// the Hangfire worker is freed up immediately; the Halibut queue does NOT
/// grow. Operator sees structured Serilog warnings and can alert.</para>
///
/// <para>Tests use the strategy's static admission helpers
/// (<see cref="HalibutPollingWorkAdmission"/>) directly — the gate is a
/// pure data-structure operation independent of the rest of the dispatch
/// pipeline, so unit-testable without the full execution machinery.</para>
/// </summary>
[Collection("HalibutPollingWorkAdmissionStaticState")]
public sealed class HalibutPollingWorkAdmissionTests : IDisposable
{
    public HalibutPollingWorkAdmissionTests() => HalibutPollingWorkAdmission.ResetForTests();
    public void Dispose() => HalibutPollingWorkAdmission.ResetForTests();

    // ── Operator-facing env var name pinned (Rule 8) ─────────────────────────

    [Fact]
    public void MaxPendingWorkPerAgentEnvVar_ConstantNamePinned()
    {
        // Operators reference this in deploy templates / Helm overrides.
        // Renaming breaks every Squid tenant who tuned the cap.
        HalibutPollingWorkAdmission.MaxPendingWorkPerAgentEnvVar
            .ShouldBe("SQUID_HALIBUT_MAX_PENDING_WORK_PER_AGENT");
    }

    [Fact]
    public void DefaultMaxPendingWorkPerAgent_Is100()
    {
        // 100 chosen because:
        //   - typical tenant deploys ~10 actions/deploy across ~5 envs = 50/burst
        //   - 100 leaves 2x headroom for legit burst before reject kicks in
        //   - well below the OOM threshold for realistic per-machine queue size
        HalibutPollingWorkAdmission.DefaultMaxPendingWorkPerAgent.ShouldBe(100);
    }

    // ── Admission gate semantics ─────────────────────────────────────────────

    [Fact]
    public void Admit_BelowLimit_Allows()
    {
        const int machineId = 1;
        const int maxPending = 10;

        for (var i = 0; i < maxPending; i++)
        {
            var admitted = HalibutPollingWorkAdmission.TryAdmit(machineId, maxPending, out var current);
            admitted.ShouldBeTrue(customMessage: $"Admit #{i + 1}/{maxPending} should succeed");
            current.ShouldBe(i + 1);
        }
    }

    [Fact]
    public void Admit_AtLimit_Rejects()
    {
        const int machineId = 2;
        const int maxPending = 5;

        for (var i = 0; i < maxPending; i++)
            HalibutPollingWorkAdmission.TryAdmit(machineId, maxPending, out _).ShouldBeTrue();

        var admitted = HalibutPollingWorkAdmission.TryAdmit(machineId, maxPending, out var current);

        admitted.ShouldBeFalse(customMessage:
            "11th admission attempt at limit=10 must be rejected.");
        current.ShouldBe(maxPending, customMessage:
            "Rejection must NOT advance the counter past the limit (no integer-overflow leak).");
    }

    [Fact]
    public void Release_DecrementsCount_AllowsNewAdmission()
    {
        const int machineId = 3;
        const int maxPending = 2;

        HalibutPollingWorkAdmission.TryAdmit(machineId, maxPending, out _).ShouldBeTrue();
        HalibutPollingWorkAdmission.TryAdmit(machineId, maxPending, out _).ShouldBeTrue();
        HalibutPollingWorkAdmission.TryAdmit(machineId, maxPending, out _).ShouldBeFalse();

        // Release one — slot available again
        HalibutPollingWorkAdmission.Release(machineId);

        var admitted = HalibutPollingWorkAdmission.TryAdmit(machineId, maxPending, out var current);
        admitted.ShouldBeTrue(customMessage: "Slot freed by Release should be re-admittable.");
        current.ShouldBe(2);
    }

    [Fact]
    public void Release_BelowZero_Clamps()
    {
        // Defensive: a missed Admit / double-Release would otherwise leave the
        // counter at -1, permanently allowing one extra slot per machine.
        const int machineId = 4;

        HalibutPollingWorkAdmission.Release(machineId);
        HalibutPollingWorkAdmission.Release(machineId);

        // Counter must NOT go negative — TryAdmit on a fresh-from-zero machine
        // succeeds at exactly 1, regardless of prior over-Release.
        HalibutPollingWorkAdmission.TryAdmit(machineId, 5, out var current).ShouldBeTrue();
        current.ShouldBe(1, customMessage:
            "Counter must clamp at 0 on over-Release; first admit after over-Release must yield 1.");
    }

    [Fact]
    public void PerMachineIsolation_OneMachineFullDoesNotBlockAnother()
    {
        // Critical correctness: a runaway machine 1 must NOT cause machine 2's
        // (healthy) deploys to be rejected. Per-machine counters, no global cap.
        const int machineA = 100;
        const int machineB = 200;
        const int maxPending = 3;

        // Saturate machine A
        for (var i = 0; i < maxPending; i++)
            HalibutPollingWorkAdmission.TryAdmit(machineA, maxPending, out _).ShouldBeTrue();
        HalibutPollingWorkAdmission.TryAdmit(machineA, maxPending, out _).ShouldBeFalse();

        // Machine B must still be admittable from zero
        var admitted = HalibutPollingWorkAdmission.TryAdmit(machineB, maxPending, out var current);
        admitted.ShouldBeTrue(customMessage:
            "Saturating machine A must not affect machine B's admission slots.");
        current.ShouldBe(1);
    }

    // ── ResolveMaxPendingWorkPerAgent — env-var parsing ─────────────────────

    [Theory]
    [InlineData(null,         100)]  // unset → default
    [InlineData("",           100)]  // empty → default
    [InlineData("   ",        100)]  // whitespace → default
    [InlineData("50",          50)]  // explicit override
    [InlineData("1000",      1000)]  // larger override
    [InlineData("not-int",    100)]  // garbage → default + warn
    [InlineData("0",          100)]  // 0 would deadlock all dispatch → default
    [InlineData("-1",         100)]  // negative → default
    public void ParseMaxPendingWorkPerAgent_HandlesAllInputs(string raw, int expected)
    {
        HalibutPollingWorkAdmission.ParseMaxPendingWorkPerAgent(raw).ShouldBe(expected);
    }

    // ── High-concurrency stress: multiple admissions/releases interleave safely
    [Fact]
    public void Stress_ConcurrentAdmitRelease_CounterStaysCorrect()
    {
        // 50 concurrent admit→release cycles, then verify final count = 0.
        // Tests the Interlocked-based atomic increment/decrement under load.
        const int machineId = 999;
        const int iterations = 50;
        const int maxPending = iterations * 2;  // generous, all should fit

        Parallel.For(0, iterations, _ =>
        {
            HalibutPollingWorkAdmission.TryAdmit(machineId, maxPending, out _).ShouldBeTrue();
            Thread.Sleep(1);  // tiny gap to interleave
            HalibutPollingWorkAdmission.Release(machineId);
        });

        // Final state: zero in-flight (all pairs balanced)
        HalibutPollingWorkAdmission.GetCurrentCount(machineId).ShouldBe(0,
            customMessage: "After balanced concurrent admit/release, count must equal 0 (no torn updates).");
    }
}
