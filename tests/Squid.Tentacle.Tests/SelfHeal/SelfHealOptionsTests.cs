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
}
