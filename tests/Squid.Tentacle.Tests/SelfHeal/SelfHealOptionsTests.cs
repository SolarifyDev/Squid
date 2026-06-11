using Shouldly;
using Squid.Tentacle.SelfHeal;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.SelfHeal;

/// <summary>
/// Rule 8 pin for the disk-pressure self-heal tunables. The retention quota +
/// low-disk trigger are operator-tunable behaviour (a tiny-disk agent wants fewer
/// kept workspaces; a debug-heavy operator wants more), so they live behind env
/// vars with pinned <c>public const string</c> names + pinned literal defaults —
/// a "harmless" rename / default change becomes a visible, test-gated decision
/// instead of a silent prod surprise for an operator who pinned the old name.
///
/// <para>Like <c>OrphanWorkspaceCleanupConfigTests</c>, the env vars are read once
/// at process start (cached static), so these tests pin the contract (names +
/// defaults), not a runtime re-read.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class SelfHealOptionsTests
{
    [Fact]
    public void EnvVarNames_Pinned()
    {
        // Operators reference these in runbooks / Helm charts / air-gapped configs.
        SelfHealOptions.KeepSucceededEnvVar.ShouldBe("SQUID_TENTACLE_SELFHEAL_KEEP_SUCCEEDED");
        SelfHealOptions.KeepFailedEnvVar.ShouldBe("SQUID_TENTACLE_SELFHEAL_KEEP_FAILED");
        SelfHealOptions.LowFreePercentageEnvVar.ShouldBe("SQUID_TENTACLE_SELFHEAL_LOW_FREE_PCT");
    }

    [Fact]
    public void LiteralDefaults_Pinned()
    {
        // Changing any of these alters how aggressively a disk-full agent reclaims
        // workspaces — must be a documented decision, not an invisible refactor.
        SelfHealOptions.DefaultKeepLatestSucceeded.ShouldBe(10);
        SelfHealOptions.DefaultKeepLatestFailed.ShouldBe(20);
        SelfHealOptions.DefaultLowFreePercentage.ShouldBe(0.20);
        SelfHealOptions.DefaultCriticalFreePercentage.ShouldBe(0.10);
        SelfHealOptions.DefaultCriticalTargetFreePercentage.ShouldBe(0.30);
    }

    [Fact]
    public void FreshGraceWindow_Pinned()
    {
        // The TOCTOU safety floor — never delete a workspace a deployment is
        // initialising into. A change here widens/narrows the race window.
        SelfHealOptions.FreshWorkspaceGraceSeconds.ShouldBe(60);
        SelfHealOptions.FreshWorkspaceGraceWindow.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Default_WithNoEnvOverrides_UsesLiteralDefaults()
    {
        // The CI / dev environment sets none of these env vars, so Default must
        // resolve to the pinned literals — proving Resolve()'s no-override path.
        var options = SelfHealOptions.Default;

        options.KeepLatestSucceeded.ShouldBe(SelfHealOptions.DefaultKeepLatestSucceeded);
        options.KeepLatestFailed.ShouldBe(SelfHealOptions.DefaultKeepLatestFailed);
        options.LowFreePercentage.ShouldBe(SelfHealOptions.DefaultLowFreePercentage);
        options.CriticalFreePercentage.ShouldBe(SelfHealOptions.DefaultCriticalFreePercentage);
        options.CriticalTargetFreePercentage.ShouldBe(SelfHealOptions.DefaultCriticalTargetFreePercentage);
    }

    [Fact]
    public void Quota_MirrorsRetentionCounts()
    {
        var options = SelfHealOptions.Default;

        options.Quota.KeepLatestSucceeded.ShouldBe(options.KeepLatestSucceeded);
        options.Quota.KeepLatestFailed.ShouldBe(options.KeepLatestFailed);
    }

    [Fact]
    public void KeepCountBounds_Pinned()
    {
        // Mirrors LocalScriptService.Min/MaxOrphanMaxAgeHours: a widening of the cap
        // (or dropping the bounds check) must be a visible, test-gated decision.
        SelfHealOptions.MinKeepCount.ShouldBe(0);
        SelfHealOptions.MaxKeepCount.ShouldBe(10_000);
    }

    [Theory]
    [InlineData(null, 10)]      // unset → default
    [InlineData("", 10)]        // blank → default
    [InlineData("   ", 10)]     // whitespace → default
    [InlineData("abc", 10)]     // unparseable → default
    [InlineData("-1", 10)]      // below MinKeepCount → default
    [InlineData("10001", 10)]   // above MaxKeepCount → default
    [InlineData("0", 0)]        // valid lower bound (keep nothing under pressure)
    [InlineData("5", 5)]        // valid
    [InlineData("10000", 10000)] // valid upper bound
    public void ParseKeepCount_AcceptsValid_RejectsOutOfRangeOrGarbage(string raw, int expected)
        => SelfHealOptions.ParseKeepCount(raw, "SQUID_TENTACLE_SELFHEAL_KEEP_SUCCEEDED", defaultValue: 10).ShouldBe(expected);

    [Theory]
    [InlineData(null, 0.20)]     // unset → default
    [InlineData("", 0.20)]       // blank → default
    [InlineData("abc", 0.20)]    // unparseable → default
    [InlineData("0", 0.20)]      // <= 0 → default (must be a strict fraction)
    [InlineData("1", 0.20)]      // >= 1 → default
    [InlineData("-0.1", 0.20)]   // negative → default
    [InlineData("1.5", 0.20)]    // above 1 → default
    [InlineData("0.35", 0.35)]   // valid fraction
    [InlineData("0.99", 0.99)]   // valid near-upper
    public void ParseFreePercentage_AcceptsValidFraction_RejectsOutOfRangeOrGarbage(string raw, double expected)
        => SelfHealOptions.ParseFreePercentage(raw, "SQUID_TENTACLE_SELFHEAL_LOW_FREE_PCT", defaultValue: 0.20).ShouldBe(expected);

    [Fact]
    public void ParseFreePercentage_IsCultureInvariant()
    {
        // An operator's "0.20" must parse the same on a comma-decimal locale server —
        // invariant culture means the dot is always the decimal separator.
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            SelfHealOptions.ParseFreePercentage("0.30", "SQUID_TENTACLE_SELFHEAL_LOW_FREE_PCT", defaultValue: 0.20).ShouldBe(0.30);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
