using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.ProjectTriggers;
using Squid.Core.Services.ProjectTriggers.Runtime;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.UnitTests.Services.ProjectTriggers.Runtime;

public class ProjectTriggerRunPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 4, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(7, 8, 9, true)]
    [InlineData(null, 8, 9, false)]
    [InlineData(7, null, 9, false)]
    [InlineData(7, 8, null, false)]
    [InlineData(99, 8, 9, false)]
    [InlineData(7, 99, 9, false)]
    [InlineData(7, 8, 99, false)]
    public void FrozenActionMustMatchEveryConfiguredReference(int? channel, int? source, int? destination, bool matches)
    {
        var plan = new TriggerActionPlan { ChannelId = channel, SourceEnvironmentId = source, EnvironmentId = destination };

        ProjectTriggerRunPolicy.MatchesScope(plan, new TriggerResourceScope(7, 8, 9)).ShouldBe(matches);
    }

    [Fact]
    public void OptionalChannelCanResolveToAConcreteProjectChannelForAuthorization()
    {
        var plan = new TriggerActionPlan { ChannelId = 7, SourceEnvironmentId = 8, EnvironmentId = 9 };

        ProjectTriggerRunPolicy.MatchesScope(plan, new TriggerResourceScope(null, 8, 9)).ShouldBeTrue();
        ProjectTriggerRunPolicy.ActualScope(plan).ShouldBe(new TriggerResourceScope(7, 8, 9));
    }

    [Theory]
    [InlineData(ProjectTriggerRunStatuses.Pending)]
    [InlineData(ProjectTriggerRunStatuses.AwaitingDispatch)]
    public void TransientFailuresBackOffThenEndWithTheReasonAndObjectLinks(string phase)
    {
        var run = new ProjectTriggerRun { Status = phase, ReleaseId = 23, DeploymentId = 31, TaskId = 49 };
        var previousDelay = TimeSpan.Zero;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            ProjectTriggerRunPolicy.RecordFailure(run, Now, "Retryable failure.", retryable: true);

            run.Attempts.ShouldBe(attempt);
            if (attempt == 5) break;

            run.Status.ShouldBe(phase);
            var delay = run.NextAttemptAt - Now;
            delay.ShouldBeGreaterThan(previousDelay);
            previousDelay = delay;
        }

        run.Status.ShouldBe(ProjectTriggerRunStatuses.Failed);
        run.CompletedAt.ShouldBe(Now);
        run.Reason.ShouldContain("Retryable failure.");
        (run.ReleaseId, run.DeploymentId, run.TaskId).ShouldBe(((int?)23, (int?)31, (int?)49));
    }

    [Fact]
    public void PermanentFailureEndsWithoutSchedulingAnotherAttempt()
    {
        var run = new ProjectTriggerRun { Status = ProjectTriggerRunStatuses.Pending };

        ProjectTriggerRunPolicy.RecordFailure(run, Now, "Authorization denied.", retryable: false);

        run.Attempts.ShouldBe(1);
        run.Status.ShouldBe(ProjectTriggerRunStatuses.Failed);
        run.Reason.ShouldBe("Authorization denied.");
        run.CompletedAt.ShouldBe(Now);
    }
}
