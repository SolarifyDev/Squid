using System.Collections.Concurrent;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// P1-Phase9b.1 (audit item B.2) — bounded polling-work admission gate.
///
/// <para><b>Why this exists</b>: when a polling Tentacle goes offline (network
/// partition, crashed agent process), every queued deploy still hits the
/// server-side dispatch path. Without a gate, Halibut's in-memory pending-
/// request queue grows unbounded — RAM rises, OOM-killer eventually
/// terminates Squid. Pre-Phase-9b.1 there was no upper bound: 1000 deploys
/// queued for an offline agent would all sit in the queue waiting forever.</para>
///
/// <para><b>Contract</b>: per-machine in-flight counter. <see cref="TryAdmit"/>
/// atomically increments and returns false if over the limit. <see cref="Release"/>
/// atomically decrements (clamped at 0 — defensive against missed Admit /
/// double-Release).</para>
///
/// <para><b>Operator-tunable</b> via <see cref="MaxPendingWorkPerAgentEnvVar"/>.
/// Default 100 covers typical tenant burst (10 actions × 5 envs = 50/burst with
/// 2x headroom) while staying well below the OOM threshold for realistic
/// per-machine queue size.</para>
///
/// <para><b>Static state with intent</b>: counter dictionary lives across the
/// process lifetime. Per-machine ID, never deleted — the allocation is one int
/// per machine ever seen (typical fleet: 10s-1000s of machines, negligible).
/// <see cref="ResetForTests"/> exists for test isolation only; production code
/// must NEVER call it.</para>
/// </summary>
internal static class HalibutPollingWorkAdmission
{
    /// <summary>
    /// Env var that overrides <see cref="DefaultMaxPendingWorkPerAgent"/>.
    /// Pinned literal — operators reference this name in deploy templates,
    /// Helm overrides, runbooks. Renaming breaks every tenant who tuned the
    /// cap. See <c>HalibutPollingWorkAdmissionTests.MaxPendingWorkPerAgentEnvVar_ConstantNamePinned</c>.
    /// </summary>
    public const string MaxPendingWorkPerAgentEnvVar = "SQUID_HALIBUT_MAX_PENDING_WORK_PER_AGENT";

    public const int DefaultMaxPendingWorkPerAgent = 100;

    private static readonly ConcurrentDictionary<int, int> _inFlight = new();

    /// <summary>
    /// Atomic try-increment. Returns true if the new in-flight count is
    /// within <paramref name="maxPending"/>; false if the slot would exceed
    /// the limit (and the counter is NOT advanced past the limit on failure).
    /// </summary>
    public static bool TryAdmit(int machineId, int maxPending, out int currentCount)
    {
        var newCount = _inFlight.AddOrUpdate(
            machineId,
            addValueFactory: _ => 1,
            updateValueFactory: (_, existing) => existing + 1);

        if (newCount > maxPending)
        {
            // Roll back the speculative increment so the counter doesn't drift
            // past the limit on repeated rejections.
            _inFlight.AddOrUpdate(
                machineId,
                addValueFactory: _ => 0,
                updateValueFactory: (_, existing) => Math.Max(0, existing - 1));
            currentCount = maxPending;
            return false;
        }

        currentCount = newCount;
        return true;
    }

    /// <summary>
    /// Atomic decrement, clamped at 0. Safe under double-Release / missed-
    /// Admit scenarios — the counter never goes negative, so a buggy caller
    /// can't accidentally raise the effective per-machine cap.
    /// </summary>
    public static void Release(int machineId)
    {
        _inFlight.AddOrUpdate(
            machineId,
            addValueFactory: _ => 0,
            updateValueFactory: (_, existing) => Math.Max(0, existing - 1));
    }

    /// <summary>Test-only observability hook — current in-flight count for one machine.</summary>
    internal static int GetCurrentCount(int machineId)
        => _inFlight.TryGetValue(machineId, out var count) ? count : 0;

    /// <summary>Test-only reset — wipes ALL machine counters. Never call from production.</summary>
    internal static void ResetForTests() => _inFlight.Clear();

    /// <summary>
    /// Pure parser for unit testing without env state. Bounds:
    /// <list type="bullet">
    ///   <item>Unset / blank / unparseable → <see cref="DefaultMaxPendingWorkPerAgent"/>.</item>
    ///   <item>Zero or negative → <see cref="DefaultMaxPendingWorkPerAgent"/>
    ///         (zero would deadlock dispatch — defensive against operator typo).</item>
    /// </list>
    /// </summary>
    public static int ParseMaxPendingWorkPerAgent(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultMaxPendingWorkPerAgent;

        if (!int.TryParse(raw.Trim(), out var value) || value <= 0)
        {
            Log.Warning(
                "{EnvVar}='{RawValue}' is not a valid positive integer; falling back to default {Default}.",
                MaxPendingWorkPerAgentEnvVar, raw, DefaultMaxPendingWorkPerAgent);
            return DefaultMaxPendingWorkPerAgent;
        }

        return value;
    }

    /// <summary>Reads the env var and returns the resolved per-agent limit.</summary>
    public static int ResolveMaxPendingWorkPerAgent()
        => ParseMaxPendingWorkPerAgent(Environment.GetEnvironmentVariable(MaxPendingWorkPerAgentEnvVar));
}
