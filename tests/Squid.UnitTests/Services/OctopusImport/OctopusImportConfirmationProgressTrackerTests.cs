using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportConfirmationProgressTrackerTests
{
    private static readonly Guid AttemptId = Guid.NewGuid();

    [Fact]
    public void Bounds_Pinned()
    {
        // The payload is rewritten twice a second and polled by every open page: these keep it at a few KB.
        OctopusImportConfirmationProgressTracker.MaxRecentLogEntries.ShouldBe(20);
        OctopusImportConfirmationProgressTracker.MaxDisplayNameLength.ShouldBe(200);
    }

    [Fact]
    public void BeginItem_SetsCurrentWithProjectNameAndIndex()
    {
        var release = Release("1.2.3");
        var tracker = Tracker(Release("1.0.0"), release);
        tracker.CompleteItem(Release("1.0.0"), OctopusImportResourceOutcomeState.Created);

        tracker.BeginItem(release);

        var current = tracker.Snapshot().Progress.Current.ShouldNotBeNull();
        current.ShouldSatisfyAllConditions(c => c.Name.ShouldBe("1.2.3"), c => c.ProjectName.ShouldBe("Shop"), c => c.Kind.ShouldBe("Release"), c => c.Phase.ShouldBe(OctopusImportConfirmationPhase.Releases), c => c.Index.ShouldBe(2));
    }

    [Fact]
    public void CompleteItem_CountsThePhaseAndTheRun_AndLogsTheOutcome()
    {
        var release = Release("1.2.3");
        var tracker = Tracker(Environment(), release, Release("2.0.0"));
        tracker.EnterStage(OctopusImportConfirmationStage.Importing);
        tracker.BeginItem(release);

        tracker.CompleteItem(release, OctopusImportResourceOutcomeState.Created);

        var progress = tracker.Snapshot().Progress;
        progress.ShouldSatisfyAllConditions(p => p.ItemsDone.ShouldBe(1), p => p.ItemsTotal.ShouldBe(3), p => p.Current.ShouldBeNull());
        progress.Phases.Select(p => (p.Phase, p.Done, p.Total)).ShouldBe([(OctopusImportConfirmationPhase.Environments, 0, 1), (OctopusImportConfirmationPhase.Releases, 1, 2)]);
        progress.RecentLog.Last().ShouldSatisfyAllConditions(e => e.Name.ShouldBe("1.2.3"), e => e.ProjectName.ShouldBe("Shop"), e => e.Outcome.ShouldBe(OctopusImportResourceOutcomeState.Created), e => e.Phase.ShouldBe(OctopusImportConfirmationPhase.Releases), e => e.Stage.ShouldBe(OctopusImportConfirmationStage.Importing));
    }

    [Fact]
    public void UncountedKind_ChangesNothing()
    {
        var task = OctopusImportConfirmationProgressPlanTests.Node(OctopusResourceKind.ServerTask);
        var tracker = Tracker(Release("1.0.0"), task);
        var before = tracker.Version;

        tracker.BeginItem(task);
        tracker.CompleteItem(task, OctopusImportResourceOutcomeState.Created);

        tracker.Version.ShouldBe(before);
        tracker.Snapshot().Progress.ItemsTotal.ShouldBe(1);
    }

    [Fact]
    public void RecentLog_KeepsTheNewest20_NewestLast()
    {
        var releases = Enumerable.Range(1, 30).Select(i => Release($"1.0.{i}")).ToArray();
        var tracker = Tracker(releases);

        foreach (var release in releases) tracker.CompleteItem(release, OctopusImportResourceOutcomeState.Created);

        var log = tracker.Snapshot().Progress.RecentLog;
        log.Count.ShouldBe(20);
        log.First().Name.ShouldBe("1.0.11");
        log.Last().Name.ShouldBe("1.0.30");
    }

    [Fact]
    public void BeginAttempt_StartsCountingAgain()
    {
        var release = Release("1.0.0");
        var tracker = Tracker(release);
        tracker.BeginAttempt(1);
        tracker.CompleteItem(release, OctopusImportResourceOutcomeState.Created);

        tracker.BeginAttempt(2);

        var progress = tracker.Snapshot().Progress;
        progress.ShouldSatisfyAllConditions(p => p.Attempt.ShouldBe(2), p => p.ItemsDone.ShouldBe(0), p => p.RecentLog.ShouldBeEmpty(), p => p.Phases.Single().Done.ShouldBe(0), p => p.Finished.ShouldBeFalse());
    }

    [Fact]
    public void Version_ChangesOnlyOnAChange()
    {
        var tracker = Tracker(Release("1.0.0"));
        var (before, _) = tracker.Snapshot();

        tracker.Snapshot().Version.ShouldBe(before, "a snapshot is not a change");

        tracker.EnterStage(OctopusImportConfirmationStage.Importing);

        tracker.Version.ShouldBeGreaterThan(before);
    }

    [Fact]
    public void Finish_EndsTheRun_AndAgainInTheSameStageChangesNothing()
    {
        var tracker = Tracker(Release("1.0.0"));
        tracker.BeginItem(Release("1.0.0"));

        tracker.Finish(OctopusImportConfirmationStage.RolledBack);
        var version = tracker.Version;
        tracker.Finish(OctopusImportConfirmationStage.RolledBack);

        tracker.Version.ShouldBe(version);
        tracker.Snapshot().Progress.ShouldSatisfyAllConditions(p => p.Finished.ShouldBeTrue(), p => p.Stage.ShouldBe(OctopusImportConfirmationStage.RolledBack), p => p.Current.ShouldBeNull(), p => p.RecentLog.Count(e => e.Stage == OctopusImportConfirmationStage.RolledBack).ShouldBe(1));
    }

    [Fact]
    public void LongNames_TruncatedToTheBound()
    {
        var release = Release(new string('r', 500));
        var tracker = Tracker(release);

        tracker.BeginItem(release);

        var name = tracker.Snapshot().Progress.Current.Name;
        name.Length.ShouldBe(OctopusImportConfirmationProgressTracker.MaxDisplayNameLength);
        name.ShouldEndWith("…");
    }

    [Fact]
    public void Snapshot_IsACopy()
    {
        var release = Release("1.0.0");
        var tracker = Tracker(release);
        var snapshot = tracker.Snapshot().Progress;

        tracker.CompleteItem(release, OctopusImportResourceOutcomeState.Created);

        snapshot.ItemsDone.ShouldBe(0);
        snapshot.RecentLog.ShouldBeEmpty();
        snapshot.AttemptId.ShouldBe(AttemptId);
    }

    private static OctopusImportConfirmationProgressTracker Tracker(params OctopusResourceNode[] ordered)
        => new(AttemptId, ordered, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Projects-1"] = "Shop" }, TimeProvider.System);

    private static OctopusResourceNode Release(string version) => new($"Releases-{version}", version, OctopusResourceKind.Release, default, "path", "Projects-1", null, true, null);

    private static OctopusResourceNode Environment() => OctopusImportConfirmationProgressPlanTests.Node(OctopusResourceKind.Environment);
}
