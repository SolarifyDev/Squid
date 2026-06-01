namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Classifies an agent-reported upgrade <see cref="UpgradeStatusPayload.Status"/>
/// string as <b>in-flight</b> (the upgrade is still progressing — more status
/// transitions are expected) or <b>terminal</b> (the upgrade attempt has
/// concluded — no further automated transitions will come).
///
/// <para><b>Why "not in-flight" instead of an explicit terminal allow-list:</b>
/// the agent's upgrade scripts already emit a known, <i>small</i> set of
/// in-flight markers (<see cref="InFlightStatuses"/>) and a larger, evolving
/// set of terminal markers (SUCCESS / FAILED / ROLLED_BACK /
/// ROLLBACK_NEEDED / ROLLBACK_CRITICAL_FAILED / ...). Defining terminal as
/// "non-empty AND not one of the few in-flight markers" means a NEW terminal
/// status added to a future agent script is automatically treated as terminal
/// by older servers — no server-side drift. The cost of being wrong is benign:
/// the durable-trace persister only ever <i>writes a snapshot</i>, so
/// over-classifying an unknown status as terminal at worst persists one extra
/// (still-accurate) snapshot.</para>
///
/// <para>Comparison is ordinal + case-sensitive because the agent scripts emit
/// these tokens as fixed upper-case literals (see <c>upgrade-linux-tentacle.sh</c>
/// <c>write_status</c> calls and <c>upgrade-windows-tentacle.ps1</c>
/// <c>Write-Status</c>). The in-flight token values are pinned by unit test so a
/// rename on either side is a test-time-visible decision.</para>
/// </summary>
public static class UpgradeStatusClassifier
{
    /// <summary>Upgrade started; method selection / download / verification underway.</summary>
    public const string InProgress = "IN_PROGRESS";

    /// <summary>New binary swapped into place; service restart imminent.</summary>
    public const string Swapped = "SWAPPED";

    /// <summary>Health check failed after restart; previous version being restored.</summary>
    public const string RollingBack = "ROLLING_BACK";

    /// <summary>
    /// The complete set of statuses that mean "still progressing — do NOT treat
    /// as a final outcome". Ordinal set; any non-empty status outside this set
    /// is terminal. Pinned by <c>UpgradeStatusClassifierTests</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> InFlightStatuses =
        new HashSet<string>(StringComparer.Ordinal) { InProgress, Swapped, RollingBack };

    /// <summary>
    /// True when <paramref name="status"/> represents a concluded upgrade
    /// attempt — i.e. it is non-empty and is not one of the
    /// <see cref="InFlightStatuses"/>. Whitespace / null / empty is NOT terminal
    /// (a fresh agent that never upgraded, or a status file the agent hasn't
    /// written yet, has nothing worth persisting).
    /// </summary>
    public static bool IsTerminal(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;

        return !InFlightStatuses.Contains(status);
    }
}
