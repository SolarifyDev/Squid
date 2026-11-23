using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.ServerTask.Lease;

/// <summary>
/// A task whose server stopped is paused by the reaper and then resumed by it. The lease fence drops a redelivery of the task's job while the dead owner's
/// lease is still young, and Hangfire does not redeliver it again, so without this nothing would ever resume the task. Deleting the resume call, or its cap,
/// fails these.
/// </summary>
public class ServerTaskLeaseReaperAutoResumeTests
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly Mock<IServerTaskLeaseDataProvider> _leases = new();
    private readonly Mock<IServerTaskService> _taskService = new();
    private readonly Mock<IServerTaskControlService> _control = new();

    [Fact]
    public async Task ARecoveredExecutingTask_IsResumedAutomatically()
    {
        Recover(new StaleServerTask(7, TaskState.Executing, "pod-a:1:x"), TaskState.Paused, 1, earlierRecoveries: 0);

        (await Service().ReapStaleAsync()).ShouldBe(1);

        _control.Verify(c => c.TryAutoResumeAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        _taskService.Verify(s => s.AddLogAsync(7, It.IsAny<long>(), ServerTaskLogCategory.Warning, It.Is<string>(m => m.Contains("resumed automatically")), ServerTaskLeaseReaperService.LogSource, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The resume is requested only after the recovery landed: the task must be Paused for the resume's claim to apply.
    [Fact]
    public async Task TheResume_IsRequestedAfterTheRecoveryWasCommitted()
    {
        var order = new List<string>();
        var task = new StaleServerTask(7, TaskState.Executing, "pod-a:1:x");
        Recover(task, TaskState.Paused, 1, earlierRecoveries: 0);
        _leases.Setup(l => l.RecoverAsync(task, TaskState.Paused, StaleAfter, It.IsAny<CancellationToken>())).Callback(() => order.Add("recover")).ReturnsAsync(1);
        _control.Setup(c => c.TryAutoResumeAsync(7, It.IsAny<CancellationToken>())).Callback(() => order.Add("resume")).Returns(Task.CompletedTask);

        await Service().ReapStaleAsync();

        order.ShouldBe(["recover", "resume"]);
    }

    [Fact]
    public async Task ACancellingTask_IsCompletedAsCancelled_NeverResumed()
    {
        Recover(new StaleServerTask(8, TaskState.Cancelling, "pod-a:1:x"), TaskState.Cancelled, 1, earlierRecoveries: 0);

        await Service().ReapStaleAsync();

        _control.Verify(c => c.TryAutoResumeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A renewal that won the race means the owner is alive and nothing was recovered: nothing to resume.
    [Fact]
    public async Task ALostRace_ResumesNothing()
    {
        Recover(new StaleServerTask(7, TaskState.Executing, "pod-a:1:x"), TaskState.Paused, 0, earlierRecoveries: 0);

        (await Service().ReapStaleAsync()).ShouldBe(0);

        _control.Verify(c => c.TryAutoResumeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A deployment that keeps stopping its server must not loop forever: after MaxAutoResumes automatic resumes it waits for a human.
    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(9, false)]
    public async Task ResumeIsCapped_PerTask_ByTheEarlierRecoveries(int earlierRecoveries, bool resumes)
    {
        var task = new StaleServerTask(7, TaskState.Executing, "pod-a:1:x");
        Recover(task, TaskState.Paused, 1, earlierRecoveries);

        (await Service(maxAutoResumes: 3).ReapStaleAsync()).ShouldBe(1, "the task is paused either way");

        _control.Verify(c => c.TryAutoResumeAsync(7, It.IsAny<CancellationToken>()), resumes ? Times.Once : Times.Never);
        var expected = resumes ? "resumed automatically" : "resume it manually";
        _taskService.Verify(s => s.AddLogAsync(7, It.IsAny<long>(), ServerTaskLogCategory.Warning, It.Is<string>(m => m.Contains(expected)), ServerTaskLeaseReaperService.LogSource, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AFailingResume_DoesNotHideTheRecovery_OrStopTheSweep()
    {
        var bad = new StaleServerTask(1, TaskState.Executing, "a");
        var good = new StaleServerTask(2, TaskState.Executing, "b");
        _leases.Setup(l => l.GetStaleAsync(StaleAfter, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([bad, good]);
        foreach (var task in new[] { bad, good })
        {
            _leases.Setup(l => l.RecoverAsync(task, TaskState.Paused, StaleAfter, It.IsAny<CancellationToken>())).ReturnsAsync(1);
            _leases.Setup(l => l.CountLogsFromSourceAsync(task.Id, ServerTaskLeaseReaperService.LogSource, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        }
        _control.Setup(c => c.TryAutoResumeAsync(1, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("job storage down"));

        (await Service().ReapStaleAsync()).ShouldBe(2);

        _control.Verify(c => c.TryAutoResumeAsync(2, It.IsAny<CancellationToken>()), Times.Once);
    }

    // When the earlier recoveries cannot be counted, the safe answer is the old behaviour: stay Paused for an operator.
    [Fact]
    public async Task WhenTheEarlierRecoveriesCannotBeCounted_TheTaskStaysPaused()
    {
        var task = new StaleServerTask(7, TaskState.Executing, "pod-a:1:x");
        Recover(task, TaskState.Paused, 1, earlierRecoveries: 0);
        _leases.Setup(l => l.CountLogsFromSourceAsync(7, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db blip"));

        (await Service().ReapStaleAsync()).ShouldBe(1);

        _control.Verify(c => c.TryAutoResumeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A reaper wired without the control service (older construction) keeps the previous behaviour and its log text.
    [Fact]
    public async Task WithoutAControlService_TheTaskIsOnlyPaused_AsBefore()
    {
        var task = new StaleServerTask(7, TaskState.Executing, "pod-a:1:x");
        Recover(task, TaskState.Paused, 1, earlierRecoveries: 0);
        var service = new ServerTaskLeaseReaperService(_leases.Object, _taskService.Object, Mock.Of<IDeploymentCheckpointService>(), Mock.Of<IDeploymentInterruptionService>(), new ServerTaskLeaseOptions { Stale = StaleAfter });

        (await service.ReapStaleAsync()).ShouldBe(1);

        _taskService.Verify(s => s.AddLogAsync(7, It.IsAny<long>(), ServerTaskLogCategory.Warning, It.Is<string>(m => m.Contains("resume it to continue")), ServerTaskLeaseReaperService.LogSource, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private void Recover(StaleServerTask task, string to, int rows, int earlierRecoveries)
    {
        _leases.Setup(l => l.GetStaleAsync(StaleAfter, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([task]);
        _leases.Setup(l => l.RecoverAsync(task, to, StaleAfter, It.IsAny<CancellationToken>())).ReturnsAsync(rows);
        _leases.Setup(l => l.CountLogsFromSourceAsync(task.Id, ServerTaskLeaseReaperService.LogSource, It.IsAny<CancellationToken>())).ReturnsAsync(earlierRecoveries);
    }

    private ServerTaskLeaseReaperService Service(int maxAutoResumes = 3)
        => new(_leases.Object, _taskService.Object, Mock.Of<IDeploymentCheckpointService>(), Mock.Of<IDeploymentInterruptionService>(), new ServerTaskLeaseOptions { Stale = StaleAfter, MaxAutoResumes = maxAutoResumes }, Mock.Of<IInFlightScriptCanceller>(), _control.Object);
}
