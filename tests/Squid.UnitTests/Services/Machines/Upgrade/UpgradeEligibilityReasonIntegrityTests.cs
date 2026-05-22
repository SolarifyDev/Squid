using Shouldly;
using Squid.Message.Enums;
using Xunit;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Wire-stability pin (Rule 8 — operator-facing surface) for
/// <see cref="UpgradeEligibilityReason"/>. The enum's <b>names</b> AND
/// <b>numeric values</b> are serialised over the upgrade-info API and consumed
/// by the FE + any external SDK. Renaming a member breaks string-based
/// parsing; re-ordering breaks anything that serialises by ordinal. Pin both
/// here so a refactor becomes a test-time-visible decision.
///
/// <para><b>Convention</b>: add new members at the END only; never re-number
/// existing members. If a member becomes obsolete, mark it <c>[Obsolete]</c>
/// but DO NOT delete or renumber.</para>
/// </summary>
public sealed class UpgradeEligibilityReasonIntegrityTests
{
    [Theory]
    [InlineData(UpgradeEligibilityReason.NoCommunicationStyle, 0)]
    [InlineData(UpgradeEligibilityReason.NoOsDetected, 1)]
    [InlineData(UpgradeEligibilityReason.StyleNotSupported, 2)]
    [InlineData(UpgradeEligibilityReason.RegistryUnreachable, 3)]
    [InlineData(UpgradeEligibilityReason.EligibleCurrentVersionUnknown, 4)]
    [InlineData(UpgradeEligibilityReason.EligibleNonSemverComparison, 5)]
    [InlineData(UpgradeEligibilityReason.Eligible, 6)]
    [InlineData(UpgradeEligibilityReason.AlreadyUpToDate, 7)]
    [InlineData(UpgradeEligibilityReason.WouldBeDowngrade, 8)]
    public void NumericValue_PinnedToOrdinal(UpgradeEligibilityReason reason, int expected)
    {
        // Pinning numeric values defends against accidental re-ordering. Any
        // change to this table is an API break — the test forces the author
        // to think about backward compat (vs. silently shipping the break).
        ((int)reason).ShouldBe(expected);
    }

    [Theory]
    [InlineData(UpgradeEligibilityReason.NoCommunicationStyle, "NoCommunicationStyle")]
    [InlineData(UpgradeEligibilityReason.NoOsDetected, "NoOsDetected")]
    [InlineData(UpgradeEligibilityReason.StyleNotSupported, "StyleNotSupported")]
    [InlineData(UpgradeEligibilityReason.RegistryUnreachable, "RegistryUnreachable")]
    [InlineData(UpgradeEligibilityReason.EligibleCurrentVersionUnknown, "EligibleCurrentVersionUnknown")]
    [InlineData(UpgradeEligibilityReason.EligibleNonSemverComparison, "EligibleNonSemverComparison")]
    [InlineData(UpgradeEligibilityReason.Eligible, "Eligible")]
    [InlineData(UpgradeEligibilityReason.AlreadyUpToDate, "AlreadyUpToDate")]
    [InlineData(UpgradeEligibilityReason.WouldBeDowngrade, "WouldBeDowngrade")]
    public void NameLiteral_Pinned(UpgradeEligibilityReason reason, string expected)
    {
        // Pinning the literal string defends against rename refactors that
        // would silently break clients parsing the response. JSON serialisation
        // uses the member name, so a rename here is a wire-protocol break.
        reason.ToString().ShouldBe(expected);
    }

    [Fact]
    public void TotalCount_PinnedToCurrentSize()
    {
        // Any new member added to the enum must come with a test row above
        // AND bump this count. The mismatch forces the author to consciously
        // update the integrity tests rather than skip them.
        System.Enum.GetValues<UpgradeEligibilityReason>().Length.ShouldBe(9,
            customMessage: "If you're adding a new UpgradeEligibilityReason, also: " +
                           "(1) append at the END (never re-number), " +
                           "(2) add [InlineData] rows to NumericValue_PinnedToOrdinal and NameLiteral_Pinned, " +
                           "(3) bump this expected count. See class docstring.");
    }
}
