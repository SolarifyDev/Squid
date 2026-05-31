using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Message.Enums.Deployments;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.UnitTests.Services.Deployments.Retention;

public class RetentionPolicyEnforcerTests
{
    // ─── GetDeploymentsExceedingRetention (pure static) ───

    [Fact]
    public void DaysRetention_DeploymentsOlderThanCutoff_Returned()
    {
        var now = DateTimeOffset.UtcNow;
        var deployments = new List<Deployment>
        {
            MakeDeployment(1, releaseId: 100, created: now.AddDays(-1)),   // within retention
            MakeDeployment(2, releaseId: 101, created: now.AddDays(-5)),   // within retention
            MakeDeployment(3, releaseId: 102, created: now.AddDays(-15)),  // exceeds 10 days
            MakeDeployment(4, releaseId: 103, created: now.AddDays(-30))   // exceeds 10 days
        };
        var currentlyDeployed = new HashSet<int>();

        var result = RetentionPolicyEnforcer.GetDeploymentsExceedingRetention(
            deployments, RetentionPolicyUnit.Days, 10, currentlyDeployed);

        result.Count.ShouldBe(2);
        result.ShouldContain(d => d.Id == 3);
        result.ShouldContain(d => d.Id == 4);
    }

    [Fact]
    public void WeeksRetention_CalculatesCorrectCutoff()
    {
        var now = DateTimeOffset.UtcNow;
        var deployments = new List<Deployment>
        {
            MakeDeployment(1, releaseId: 100, created: now.AddDays(-6)),   // within 1 week
            MakeDeployment(2, releaseId: 101, created: now.AddDays(-8))    // exceeds 1 week
        };

        var result = RetentionPolicyEnforcer.GetDeploymentsExceedingRetention(
            deployments, RetentionPolicyUnit.Weeks, 1, new HashSet<int>());

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(2);
    }

    [Fact]
    public void MonthsRetention_CalculatesCorrectCutoff()
    {
        var now = DateTimeOffset.UtcNow;
        var deployments = new List<Deployment>
        {
            MakeDeployment(1, releaseId: 100, created: now.AddDays(-15)),  // within 1 month
            MakeDeployment(2, releaseId: 101, created: now.AddMonths(-2))  // exceeds 1 month
        };

        var result = RetentionPolicyEnforcer.GetDeploymentsExceedingRetention(
            deployments, RetentionPolicyUnit.Months, 1, new HashSet<int>());

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(2);
    }

    [Fact]
    public void CurrentlyDeployedRelease_AlwaysPreserved()
    {
        var now = DateTimeOffset.UtcNow;
        var deployments = new List<Deployment>
        {
            MakeDeployment(1, releaseId: 100, created: now.AddDays(-100)),  // old, but currently deployed
            MakeDeployment(2, releaseId: 101, created: now.AddDays(-100))   // old, not deployed
        };
        var currentlyDeployed = new HashSet<int> { 100 };

        var result = RetentionPolicyEnforcer.GetDeploymentsExceedingRetention(
            deployments, RetentionPolicyUnit.Days, 10, currentlyDeployed);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(2);
        result.ShouldNotContain(d => d.ReleaseId == 100);
    }

    [Fact]
    public void EmptyDeployments_ReturnsEmpty()
    {
        var result = RetentionPolicyEnforcer.GetDeploymentsExceedingRetention(
            new List<Deployment>(), RetentionPolicyUnit.Days, 10, new HashSet<int>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void AllWithinRetention_ReturnsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var deployments = new List<Deployment>
        {
            MakeDeployment(1, releaseId: 100, created: now.AddDays(-1)),
            MakeDeployment(2, releaseId: 101, created: now.AddDays(-2))
        };

        var result = RetentionPolicyEnforcer.GetDeploymentsExceedingRetention(
            deployments, RetentionPolicyUnit.Days, 30, new HashSet<int>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void YearsRetention_CalculatesCorrectCutoff()
    {
        var now = DateTimeOffset.UtcNow;
        var deployments = new List<Deployment>
        {
            MakeDeployment(1, releaseId: 100, created: now.AddMonths(-6)),   // within 1 year
            MakeDeployment(2, releaseId: 101, created: now.AddYears(-2))     // exceeds 1 year
        };

        var result = RetentionPolicyEnforcer.GetDeploymentsExceedingRetention(
            deployments, RetentionPolicyUnit.Years, 1, new HashSet<int>());

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(2);
    }

    // ─── GetDeploymentsExceedingRetention: multiple currently deployed ───

    [Fact]
    public void MultipleCurrentlyDeployedReleases_AllPreserved()
    {
        var now = DateTimeOffset.UtcNow;
        var deployments = new List<Deployment>
        {
            MakeDeployment(1, releaseId: 100, created: now.AddDays(-100)),
            MakeDeployment(2, releaseId: 101, created: now.AddDays(-100)),
            MakeDeployment(3, releaseId: 102, created: now.AddDays(-100))
        };
        var currentlyDeployed = new HashSet<int> { 100, 101 };

        var result = RetentionPolicyEnforcer.GetDeploymentsExceedingRetention(
            deployments, RetentionPolicyUnit.Days, 10, currentlyDeployed);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(3);
    }

    [Fact]
    public void AllCurrentlyDeployed_ReturnsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var deployments = new List<Deployment>
        {
            MakeDeployment(1, releaseId: 100, created: now.AddDays(-100)),
            MakeDeployment(2, releaseId: 101, created: now.AddDays(-100))
        };
        var currentlyDeployed = new HashSet<int> { 100, 101 };

        var result = RetentionPolicyEnforcer.GetDeploymentsExceedingRetention(
            deployments, RetentionPolicyUnit.Days, 10, currentlyDeployed);

        result.ShouldBeEmpty();
    }

    // ─── Helpers ───

    private static Deployment MakeDeployment(int id, int releaseId, DateTimeOffset created)
    {
        return new Deployment
        {
            Id = id,
            ReleaseId = releaseId,
            CreatedDate = created,
            ProjectId = 1,
            EnvironmentId = 1
        };
    }

    // ─── GetReleasesExceedingRetention (pure static) ───

    [Fact]
    public void Release_OldUndeployedNotCurrent_Pruned()
    {
        var now = DateTimeOffset.UtcNow;

        var result = RetentionPolicyEnforcer.GetReleasesExceedingRetention(
            new List<ReleaseEntity> { MakeRelease(1, now.AddDays(-40)) },
            cutoff: now.AddDays(-30), new HashSet<int>(), new HashSet<int>());

        result.Select(r => r.Id).ShouldBe(new[] { 1 });
    }

    [Fact]
    public void Release_CurrentlyDeployed_Kept()
    {
        var now = DateTimeOffset.UtcNow;

        var result = RetentionPolicyEnforcer.GetReleasesExceedingRetention(
            new List<ReleaseEntity> { MakeRelease(1, now.AddDays(-100)) },
            cutoff: now.AddDays(-30), new HashSet<int> { 1 }, new HashSet<int>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Release_WithSurvivingDeployments_Kept()
    {
        var now = DateTimeOffset.UtcNow;

        var result = RetentionPolicyEnforcer.GetReleasesExceedingRetention(
            new List<ReleaseEntity> { MakeRelease(1, now.AddDays(-100)) },
            cutoff: now.AddDays(-30), new HashSet<int>(), new HashSet<int> { 1 });

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Release_Recent_Kept()
    {
        var now = DateTimeOffset.UtcNow;

        var result = RetentionPolicyEnforcer.GetReleasesExceedingRetention(
            new List<ReleaseEntity> { MakeRelease(1, now.AddDays(-5)) },
            cutoff: now.AddDays(-30), new HashSet<int>(), new HashSet<int>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Releases_MixedSet_OnlyEligiblePruned()
    {
        var now = DateTimeOffset.UtcNow;
        var releases = new List<ReleaseEntity>
        {
            MakeRelease(1, now.AddDays(-40)),  // old, undeployed, not current → prune
            MakeRelease(2, now.AddDays(-40)),  // old but currently deployed → keep
            MakeRelease(3, now.AddDays(-40)),  // old but has surviving deployments → keep
            MakeRelease(4, now.AddDays(-5))    // recent → keep
        };

        var result = RetentionPolicyEnforcer.GetReleasesExceedingRetention(
            releases, cutoff: now.AddDays(-30), new HashSet<int> { 2 }, new HashSet<int> { 3 });

        result.Select(r => r.Id).ShouldBe(new[] { 1 });
    }

    private static ReleaseEntity MakeRelease(int id, DateTimeOffset created)
    {
        return new ReleaseEntity { Id = id, ProjectId = 1, CreatedDate = created };
    }

    private static ReleaseEntity MakeReleaseInChannel(int id, int channelId, DateTimeOffset created)
    {
        return new ReleaseEntity { Id = id, ProjectId = 1, ChannelId = channelId, CreatedDate = created };
    }

    // ─── GetReleasesExceedingCount (pure static) ───

    [Fact]
    public void Count_KeepNewestNPerChannel_OlderPruned()
    {
        var now = DateTimeOffset.UtcNow;
        var releases = new List<ReleaseEntity>
        {
            MakeRelease(1, now.AddDays(-1)),   // newest
            MakeRelease(2, now.AddDays(-2)),
            MakeRelease(3, now.AddDays(-3)),
            MakeRelease(4, now.AddDays(-4)),
            MakeRelease(5, now.AddDays(-5))    // oldest
        };

        var result = RetentionPolicyEnforcer.GetReleasesExceedingCount(releases, keepCount: 2, new HashSet<int>());

        result.Select(r => r.Id).OrderBy(id => id).ShouldBe(new[] { 3, 4, 5 });
    }

    [Fact]
    public void Count_PerChannel_KeptIndependently()
    {
        var now = DateTimeOffset.UtcNow;
        var releases = new List<ReleaseEntity>
        {
            MakeReleaseInChannel(1, channelId: 10, now.AddDays(-1)),
            MakeReleaseInChannel(2, channelId: 10, now.AddDays(-2)),
            MakeReleaseInChannel(3, channelId: 10, now.AddDays(-3)),
            MakeReleaseInChannel(4, channelId: 20, now.AddDays(-1)),
            MakeReleaseInChannel(5, channelId: 20, now.AddDays(-2)),
            MakeReleaseInChannel(6, channelId: 20, now.AddDays(-3))
        };

        var result = RetentionPolicyEnforcer.GetReleasesExceedingCount(releases, keepCount: 1, new HashSet<int>());

        // newest of each channel (1 and 4) survives; the rest is pruned
        result.Select(r => r.Id).OrderBy(id => id).ShouldBe(new[] { 2, 3, 5, 6 });
    }

    [Fact]
    public void Count_PreservedReleaseBeyondWindow_Kept()
    {
        var now = DateTimeOffset.UtcNow;
        var releases = new List<ReleaseEntity>
        {
            MakeRelease(1, now.AddDays(-1)),
            MakeRelease(2, now.AddDays(-2)),
            MakeRelease(3, now.AddDays(-3)),   // beyond keep=2 but preserved (deployed / mid-flight)
            MakeRelease(4, now.AddDays(-4))    // beyond keep=2 → prune
        };
        var preserved = new HashSet<int> { 3 };

        var result = RetentionPolicyEnforcer.GetReleasesExceedingCount(releases, keepCount: 2, preserved);

        result.Select(r => r.Id).ShouldBe(new[] { 4 });
    }

    [Fact]
    public void Count_FewerThanKeepCount_NonePruned()
    {
        var now = DateTimeOffset.UtcNow;
        var releases = new List<ReleaseEntity>
        {
            MakeRelease(1, now.AddDays(-1)),
            MakeRelease(2, now.AddDays(-2))
        };

        var result = RetentionPolicyEnforcer.GetReleasesExceedingCount(releases, keepCount: 5, new HashSet<int>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Count_TieOnCreatedDate_BrokenByIdDescending()
    {
        var ts = DateTimeOffset.UtcNow.AddDays(-1);
        var releases = new List<ReleaseEntity>
        {
            MakeRelease(1, ts),
            MakeRelease(2, ts)   // same instant; higher id ranks newer
        };

        var result = RetentionPolicyEnforcer.GetReleasesExceedingCount(releases, keepCount: 1, new HashSet<int>());

        result.Select(r => r.Id).ShouldBe(new[] { 1 });
    }

    [Fact]
    public void Count_EmptyReleases_ReturnsEmpty()
    {
        var result = RetentionPolicyEnforcer.GetReleasesExceedingCount(new List<ReleaseEntity>(), keepCount: 3, new HashSet<int>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Count_AllBeyondWindowButAllPreserved_ReturnsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var releases = new List<ReleaseEntity>
        {
            MakeRelease(1, now.AddDays(-10)),
            MakeRelease(2, now.AddDays(-20))
        };
        var preserved = new HashSet<int> { 1, 2 };

        var result = RetentionPolicyEnforcer.GetReleasesExceedingCount(releases, keepCount: 1, preserved);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Count_KeepZero_PrunesAllNonPreserved()
    {
        var now = DateTimeOffset.UtcNow;
        var releases = new List<ReleaseEntity>
        {
            MakeRelease(1, now.AddDays(-1)),
            MakeRelease(2, now.AddDays(-2))
        };

        // Pure-function contract: keepCount 0 keeps none. The "<=0 means no-op" policy lives in the
        // caller (PruneReleasesByCountAsync), not here.
        var result = RetentionPolicyEnforcer.GetReleasesExceedingCount(releases, keepCount: 0, new HashSet<int>());

        result.Select(r => r.Id).OrderBy(id => id).ShouldBe(new[] { 1, 2 });
    }

    [Fact]
    public void ItemsUnit_GetDeploymentsExceedingRetention_ReturnsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var deployments = new List<Deployment>
        {
            MakeDeployment(1, releaseId: 100, created: now.AddDays(-100))
        };

        // Items mode cleans up deployments via the release cascade, NOT via per-environment
        // deployment pruning — so this path must return nothing for Items.
        var result = RetentionPolicyEnforcer.GetDeploymentsExceedingRetention(
            deployments, RetentionPolicyUnit.Items, 2, new HashSet<int>());

        result.ShouldBeEmpty();
    }
}
