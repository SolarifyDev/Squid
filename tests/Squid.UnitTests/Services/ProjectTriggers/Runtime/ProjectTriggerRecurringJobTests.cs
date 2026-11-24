using Squid.Core.Jobs.RecurringJobs;
using Squid.Core.Services.ProjectTriggers.Runtime;

namespace Squid.UnitTests.Services.ProjectTriggers.Runtime;

public class ProjectTriggerRecurringJobTests
{
    [Fact]
    public async Task UnfinishedExternalScanDoesNotDelayAlreadyDueActionsAndDispatches()
    {
        var checking = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new Mock<IProjectTriggerDueScanner>();
        scanner.Setup(s => s.ScanDueAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(checking.Task);
        var worker = new Mock<IProjectTriggerWorker>();
        var job = new ProjectTriggerRecurringJob(scanner.Object, worker.Object);

        var running = job.Execute();
        worker.Verify(w => w.ExecutePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        worker.Verify(w => w.DispatchPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        checking.SetResult(0);
        await running;
    }

    [Fact]
    public async Task EveryMinuteScansThenExecutesAndDispatchesOneSmallBatch()
    {
        var calls = new List<string>();
        var scanner = new Mock<IProjectTriggerDueScanner>();
        var worker = new Mock<IProjectTriggerWorker>();
        scanner.Setup(s => s.ScanDueAsync(It.IsInRange(1, 50, Moq.Range.Inclusive), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("scan")).ReturnsAsync(0);
        worker.Setup(w => w.ExecutePendingAsync(It.IsInRange(1, 50, Moq.Range.Inclusive), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("execute")).ReturnsAsync(0);
        worker.Setup(w => w.DispatchPendingAsync(It.IsInRange(1, 50, Moq.Range.Inclusive), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("dispatch")).ReturnsAsync(0);
        var job = new ProjectTriggerRecurringJob(scanner.Object, worker.Object);

        await job.Execute();

        job.JobId.ShouldBe("project-triggers");
        job.CronExpression.ShouldBe("* * * * *");
        calls.ShouldBe(new[] { "scan", "execute", "dispatch" });
    }
}
