using Shouldly;
using Squid.Core.Services.Deployments.Rollback;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Rollback;

/// <summary>
/// PR-12 — unit tests for <see cref="RollbackTargetSelector"/>. Pins the
/// "which release do we roll back to?" rule against every history shape the
/// resolver can hand it.
/// </summary>
public sealed class RollbackTargetSelectorTests
{
    [Fact]
    public void NoHistory_ReturnsNull()
        => RollbackTargetSelector.SelectPreviousDistinctRelease(Array.Empty<RollbackReleaseHistoryEntry>())
            .ShouldBeNull();

    [Fact]
    public void NullHistory_ReturnsNull()
        => RollbackTargetSelector.SelectPreviousDistinctRelease(null!).ShouldBeNull();

    [Fact]
    public void SingleRelease_NothingToRollBackTo_ReturnsNull()
    {
        var history = new[] { Entry(releaseId: 5, version: "1.0.0", deploymentId: 100, minutesAgo: 0) };

        RollbackTargetSelector.SelectPreviousDistinctRelease(history).ShouldBeNull();
    }

    [Fact]
    public void SameReleaseDeployedRepeatedly_ReturnsNull()
    {
        // Re-deploying the SAME release several times is not a rollback target —
        // there is no prior *distinct* release to go back to.
        var history = new[]
        {
            Entry(releaseId: 5, version: "1.0.0", deploymentId: 102, minutesAgo: 0),
            Entry(releaseId: 5, version: "1.0.0", deploymentId: 101, minutesAgo: 10),
            Entry(releaseId: 5, version: "1.0.0", deploymentId: 100, minutesAgo: 20)
        };

        RollbackTargetSelector.SelectPreviousDistinctRelease(history).ShouldBeNull();
    }

    [Fact]
    public void LinearHistory_ReturnsImmediatelyPrecedingRelease()
    {
        // v3 (current) <- v2 <- v1. Rolling back undoes the last change: v2.
        var history = new[]
        {
            Entry(releaseId: 3, version: "3.0.0", deploymentId: 300, minutesAgo: 0),
            Entry(releaseId: 2, version: "2.0.0", deploymentId: 200, minutesAgo: 10),
            Entry(releaseId: 1, version: "1.0.0", deploymentId: 100, minutesAgo: 20)
        };

        var target = RollbackTargetSelector.SelectPreviousDistinctRelease(history);

        target.ShouldNotBeNull();
        target!.ReleaseId.ShouldBe(2);
        target.ReleaseVersion.ShouldBe("2.0.0");
        target.DeploymentId.ShouldBe(200,
            customMessage: "Target MUST carry the deployment it came from so the caller can show 'roll back to the 2.0.0 you ran at HH:MM'.");
    }

    [Fact]
    public void ReleaseReDeployedAfterNewer_RollsBackToTheDistinctPredecessor()
    {
        // Timeline: v1, then v2, then v1 again (current). The current release is
        // v1; the distinct release preceding the current state is v2.
        var history = new[]
        {
            Entry(releaseId: 1, version: "1.0.0", deploymentId: 103, minutesAgo: 0),
            Entry(releaseId: 2, version: "2.0.0", deploymentId: 102, minutesAgo: 10),
            Entry(releaseId: 1, version: "1.0.0", deploymentId: 101, minutesAgo: 20)
        };

        var target = RollbackTargetSelector.SelectPreviousDistinctRelease(history);

        target!.ReleaseId.ShouldBe(2);
        target.DeploymentId.ShouldBe(102);
    }

    [Fact]
    public void PreviousReleaseDeployedMultipleTimes_ReturnsItsMostRecentDeployment()
    {
        // v2 (current) preceded by two v1 deployments. The target is the MOST
        // RECENT v1 (deployment 101), not the older one (100).
        var history = new[]
        {
            Entry(releaseId: 2, version: "2.0.0", deploymentId: 200, minutesAgo: 0),
            Entry(releaseId: 1, version: "1.0.0", deploymentId: 101, minutesAgo: 10),
            Entry(releaseId: 1, version: "1.0.0", deploymentId: 100, minutesAgo: 20)
        };

        var target = RollbackTargetSelector.SelectPreviousDistinctRelease(history);

        target!.ReleaseId.ShouldBe(1);
        target.DeploymentId.ShouldBe(101,
            customMessage: "When the prior release was deployed multiple times, roll back to its most recent successful deployment.");
    }

    private static RollbackReleaseHistoryEntry Entry(int releaseId, string version, int deploymentId, int minutesAgo)
        => new(releaseId, version, deploymentId, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));
}
