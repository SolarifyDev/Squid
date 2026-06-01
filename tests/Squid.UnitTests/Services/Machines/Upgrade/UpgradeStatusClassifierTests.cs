using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Pins the terminal/in-flight classification that gates durable upgrade-trace
/// persistence. Getting this wrong in the "false negative" direction (treating a
/// concluded upgrade as still in-flight) means the outcome is never persisted
/// and is lost on a server restart; the "false positive" direction (treating an
/// in-flight status as terminal) would persist a non-final snapshot. Both are
/// pinned here.
/// </summary>
public sealed class UpgradeStatusClassifierTests
{
    [Theory]
    [InlineData("IN_PROGRESS")]   // upgrade started, downloading / verifying
    [InlineData("SWAPPED")]       // binary in place, restart imminent
    [InlineData("ROLLING_BACK")]  // health check failed, restoring previous version
    public void IsTerminal_InFlightStatuses_ReturnsFalse(string status)
    {
        // These statuses mean "more transitions are coming". Persisting on them
        // would write a non-final snapshot AND re-write on every probe until the
        // upgrade concludes — exactly the per-probe cost we avoid.
        UpgradeStatusClassifier.IsTerminal(status).ShouldBeFalse(
            customMessage: $"'{status}' is an in-flight status — the upgrade has not concluded, so it must NOT be treated as terminal.");
    }

    [Theory]
    [InlineData("SUCCESS")]
    [InlineData("FAILED")]
    [InlineData("ROLLED_BACK")]
    [InlineData("ROLLBACK_NEEDED")]            // manual rollback required (agent gave up)
    [InlineData("ROLLBACK_CRITICAL_FAILED")]   // agent in unknown state, manual intervention
    public void IsTerminal_ConcludedStatuses_ReturnsTrue(string status)
    {
        // Every status the Linux .sh / Windows .ps1 scripts write at the END of
        // an attempt. These are the outcomes worth persisting durably.
        UpgradeStatusClassifier.IsTerminal(status).ShouldBeTrue(
            customMessage: $"'{status}' is a concluded outcome — it must be treated as terminal so the durable trace is persisted.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsTerminal_EmptyOrWhitespace_ReturnsFalse(string status)
    {
        // A fresh agent that never upgraded, or one whose status file hasn't been
        // written yet, has nothing worth persisting.
        UpgradeStatusClassifier.IsTerminal(status).ShouldBeFalse();
    }

    [Fact]
    public void IsTerminal_UnknownNonEmptyStatus_TreatedAsTerminal()
    {
        // Drift resilience: a NEW terminal status added to a future agent script
        // (that this server build doesn't know about) must still be persisted.
        // "Not in the small, stable in-flight set" ⇒ terminal. Over-persisting an
        // unexpected status is benign (we only ever write a snapshot).
        UpgradeStatusClassifier.IsTerminal("SOME_FUTURE_TERMINAL_STATE").ShouldBeTrue(
            customMessage: "an unrecognised non-empty status must be treated as terminal so a future agent's new outcome isn't silently dropped from the durable trace.");
    }

    [Fact]
    public void IsTerminal_IsCaseSensitiveOrdinal()
    {
        // The agent emits fixed UPPER-CASE tokens. A lower-case "in_progress"
        // does NOT match the in-flight set, so by the "not in-flight ⇒ terminal"
        // rule it classifies as terminal. This pins the ordinal contract — if the
        // agent ever changed casing, this test forces the decision to be explicit.
        UpgradeStatusClassifier.IsTerminal("in_progress").ShouldBeTrue(
            customMessage: "comparison is ordinal/case-sensitive against the agent's upper-case tokens; lower-case is not a recognised in-flight marker.");
    }

    [Fact]
    public void InFlightStatuses_ConstantValues_Pinned()
    {
        // Rule 8 drift pin: these literals are the wire contract with the agent
        // scripts (upgrade-linux-tentacle.sh write_status / upgrade-windows-tentacle.ps1
        // Write-Status). A rename here without a matching agent change silently
        // breaks classification.
        UpgradeStatusClassifier.InProgress.ShouldBe("IN_PROGRESS");
        UpgradeStatusClassifier.Swapped.ShouldBe("SWAPPED");
        UpgradeStatusClassifier.RollingBack.ShouldBe("ROLLING_BACK");

        UpgradeStatusClassifier.InFlightStatuses.ShouldBe(
            new[] { "IN_PROGRESS", "SWAPPED", "ROLLING_BACK" },
            ignoreOrder: true);
    }
}
