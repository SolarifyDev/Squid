using System.Linq;
using Squid.Core.Setup.Install;
using Squid.Message.Models.Setup;

namespace Squid.UnitTests.Setup;

public sealed class SetupProgressTrackerTests
{
    [Fact]
    public void Run_FailThenRetry_CompletesAndBlocksFurtherRuns()
    {
        var tracker = new SetupProgressTracker();

        tracker.TryBegin(SetupInstaller.Steps).ShouldBeTrue();
        tracker.TryBegin(SetupInstaller.Steps).ShouldBeFalse();

        tracker.Mark(SetupInstaller.StepKeys.Database, SetupStepStatus.Failed, "boom");
        tracker.Fail("boom");

        tracker.Snapshot().ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(SetupInstallState.Failed),
            s => s.Error.ShouldBe("boom"),
            s => s.Steps[0].Status.ShouldBe(SetupStepStatus.Failed));

        tracker.TryBegin(SetupInstaller.Steps).ShouldBeTrue();
        tracker.Snapshot().Steps.ShouldAllBe(s => s.Status == SetupStepStatus.Pending);

        tracker.Complete();

        tracker.IsCompleted.ShouldBeTrue();
        tracker.TryBegin(SetupInstaller.Steps).ShouldBeFalse();
    }

    [Fact]
    public void Steps_ArePinned_SoTheWizardShowsAStableList()
    {
        SetupInstaller.Steps.Select(s => s.Key).ShouldBe(new[] { "database", "redis", "migrate", "packageStorage", "persist" });
    }
}
