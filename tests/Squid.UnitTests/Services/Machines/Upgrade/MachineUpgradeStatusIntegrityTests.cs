using Shouldly;
using Squid.Message.Commands.Machine;
using Xunit;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// H6 — wire-stability pin (Rule 8 — operator-facing surface) for
/// <see cref="MachineUpgradeStatus"/>. Values cross the upgrade API boundary
/// to the FE, CLI, and any external SDK that consumes
/// <c>UpgradeMachineResponseData.Status</c>. A rename or re-numbering
/// silently breaks every consumer parsing by ordinal or name.
///
/// <para><b>Convention</b>: new statuses MUST be appended at the END with a
/// strictly-incrementing numeric value. NEVER re-number existing members.
/// If a status becomes obsolete, mark <c>[Obsolete]</c> but don't delete or
/// renumber.</para>
/// </summary>
public sealed class MachineUpgradeStatusIntegrityTests
{
    [Theory]
    [InlineData(MachineUpgradeStatus.Upgraded, 0)]
    [InlineData(MachineUpgradeStatus.AlreadyUpToDate, 1)]
    [InlineData(MachineUpgradeStatus.NotSupported, 2)]
    [InlineData(MachineUpgradeStatus.Failed, 3)]
    [InlineData(MachineUpgradeStatus.Initiated, 4)]
    [InlineData(MachineUpgradeStatus.RolledBack, 5)]    // H6 — appended at end
    public void NumericValue_PinnedToOrdinal(MachineUpgradeStatus status, int expected)
    {
        // Re-numbering breaks every consumer that serialises by ordinal. Pin
        // values so refactoring becomes a test-visible decision.
        ((int)status).ShouldBe(expected);
    }

    [Theory]
    [InlineData(MachineUpgradeStatus.Upgraded, "Upgraded")]
    [InlineData(MachineUpgradeStatus.AlreadyUpToDate, "AlreadyUpToDate")]
    [InlineData(MachineUpgradeStatus.NotSupported, "NotSupported")]
    [InlineData(MachineUpgradeStatus.Failed, "Failed")]
    [InlineData(MachineUpgradeStatus.Initiated, "Initiated")]
    [InlineData(MachineUpgradeStatus.RolledBack, "RolledBack")]
    public void NameLiteral_Pinned(MachineUpgradeStatus status, string expected)
    {
        // JSON serialisation uses the member name; renames break every
        // string-based parser.
        status.ToString().ShouldBe(expected);
    }

    [Fact]
    public void TotalCount_PinnedToCurrentSize()
    {
        // Adding a new status MUST come with (1) [InlineData] rows in both
        // tests above and (2) a bump to this expected count. Test forces
        // conscious update vs. silent skip.
        System.Enum.GetValues<MachineUpgradeStatus>().Length.ShouldBe(6,
            customMessage: "If you're adding a new MachineUpgradeStatus, also: " +
                           "(1) append at the END (never re-number), " +
                           "(2) add [InlineData] rows to both NumericValue_PinnedToOrdinal and NameLiteral_Pinned, " +
                           "(3) bump this expected count. " +
                           "(4) decide whether the new status implies AgentVersionMayHaveChanged true/false " +
                           "(strategies set this explicitly per-construction — no silent fallthrough).");
    }
}
