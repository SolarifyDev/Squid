using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Handlers.CommandHandlers.Deployments.ServerTask;
using Squid.Core.Jobs.RecurringJobs;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Lease;
using Squid.Message.Commands.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.ServerTask.Lease;

public class ServerTaskLeaseReaperServiceTests
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly Mock<IServerTaskLeaseDataProvider> _leases = new();
    private readonly Mock<IServerTaskService> _taskService = new();
    private readonly Mock<IDeploymentCheckpointService> _checkpoints = new();
    private readonly Mock<IDeploymentInterruptionService> _interruptions = new();
    private readonly Mock<IInFlightScriptCanceller> _scriptCanceller = new();

    [Theory]
    [InlineData(TaskState.Executing, TaskState.Paused)]      // a crash must not discard progress: pause, checkpoint kept
    [InlineData(TaskState.Cancelling, TaskState.Cancelled)]  // the cancel was already requested
    [InlineData(TaskState.Paused, null)]                      // needs no live owner
    [InlineData(TaskState.Pending, null)]
    [InlineData(TaskState.Success, null)]
    [InlineData(TaskState.Failed, null)]
    [InlineData(TaskState.Cancelled, null)]
    [InlineData(TaskState.TimedOut, null)]
    public void RecoveryTarget_PerState(string state, string expected)
    {
        ServerTaskLeaseReaperService.RecoveryTarget(state).ShouldBe(expected);
    }

    [Fact]
    public async Task Executing_IsPaused_NeverFailed_CheckpointUntouched_AndALogLineExplainsWhy()
    {
        var task = new StaleServerTask(7, TaskState.Executing, "pod-a:1:x");
        Stale(task);
        Recovers(task, TaskState.Paused, 1);
        _leases.Setup(l => l.GetMaxLogSequenceAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(41);

        var recovered = await Service().ReapStaleAsync();

        recovered.ShouldBe(1);
        _leases.Verify(l => l.RecoverAsync(task, TaskState.Paused, StaleAfter, It.IsAny<CancellationToken>()), Times.Once);
        _leases.Verify(l => l.RecoverAsync(It.IsAny<StaleServerTask>(), It.Is<string>(s => s != TaskState.Paused), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        _checkpoints.Verify(c => c.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _taskService.Verify(s => s.AddLogAsync(7, 42, ServerTaskLogCategory.Warning, It.Is<string>(m => m.Contains("pod-a:1:x") && m.Contains("paused") && m.Contains("resume")), ServerTaskLeaseReaperService.LogSource, null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancelling_IsCancelled_AndItsCheckpointAndInterruptionsAreReleased()
    {
        var task = new StaleServerTask(8, TaskState.Cancelling, "pod-a:1:x");
        Stale(task);
        Recovers(task, TaskState.Cancelled, 1);

        (await Service().ReapStaleAsync()).ShouldBe(1);

        _interruptions.Verify(i => i.CancelPendingInterruptionsAsync(8, It.IsAny<CancellationToken>()), Times.Once);
        _checkpoints.Verify(c => c.DeleteAsync(8, It.IsAny<CancellationToken>()), Times.Once);
        _taskService.Verify(s => s.AddLogAsync(8, It.IsAny<long>(), ServerTaskLogCategory.Warning, It.Is<string>(m => m.Contains("Cancelled")), It.IsAny<string>(), null, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Paused/other rows are never recovered even if one slipped into the candidate list.
    [Theory]
    [InlineData(TaskState.Paused)]
    [InlineData(TaskState.Success)]
    public async Task NonRecoverableState_IsNeverRecovered(string state)
    {
        Stale(new StaleServerTask(9, state, "pod-a:1:x"));

        (await Service().ReapStaleAsync()).ShouldBe(0);

        _leases.Verify(l => l.RecoverAsync(It.IsAny<StaleServerTask>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A renewal that landed between the read and the swap wins: the reaper changes nothing and does no bookkeeping.
    [Fact]
    public async Task LostRace_IsNotCounted_AndDoesNoBookkeeping()
    {
        var task = new StaleServerTask(7, TaskState.Cancelling, "pod-a:1:x");
        Stale(task);
        Recovers(task, TaskState.Cancelled, 0);

        (await Service().ReapStaleAsync()).ShouldBe(0);

        _checkpoints.Verify(c => c.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _taskService.Verify(s => s.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OneTaskFailing_DoesNotStopTheRest()
    {
        var bad = new StaleServerTask(1, TaskState.Executing, "a");
        var good = new StaleServerTask(2, TaskState.Executing, "b");
        Stale(bad, good);
        _leases.Setup(l => l.RecoverAsync(bad, It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db blip"));
        Recovers(good, TaskState.Paused, 1);

        (await Service().ReapStaleAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task BookkeepingFailure_DoesNotUndoOrHideTheRecovery()
    {
        var task = new StaleServerTask(2, TaskState.Executing, "b");
        Stale(task);
        Recovers(task, TaskState.Paused, 1);
        _leases.Setup(l => l.GetMaxLogSequenceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("log table down"));

        (await Service().ReapStaleAsync()).ShouldBe(1);
    }

    // The owner of a Cancelling task died, so nobody is left to cancel the agent script it was running; the checkpoint deleted next is the only
    // record of the ticket, so the script must be cancelled first.
    [Fact]
    public async Task Cancelling_CancelsTheInFlightAgentScripts_BeforeTheCheckpointIsDeleted()
    {
        var order = new List<string>();
        var task = new StaleServerTask(8, TaskState.Cancelling, "pod-a:1:x");
        Stale(task);
        Recovers(task, TaskState.Cancelled, 1);
        _scriptCanceller.Setup(c => c.CancelAllAsync(8, It.IsAny<CancellationToken>())).Callback(() => order.Add("cancel-scripts")).Returns(Task.CompletedTask);
        _checkpoints.Setup(c => c.DeleteAsync(8, It.IsAny<CancellationToken>())).Callback(() => order.Add("delete-checkpoint")).Returns(Task.CompletedTask);

        await Service().ReapStaleAsync();

        order.ShouldBe(["cancel-scripts", "delete-checkpoint"]);
    }

    // An Executing task is only paused: its script must keep running for the resume to re-attach.
    [Fact]
    public async Task Executing_PausedByTheReaper_NeverCancelsTheAgentScript()
    {
        var task = new StaleServerTask(7, TaskState.Executing, "pod-a:1:x");
        Stale(task);
        Recovers(task, TaskState.Paused, 1);

        await Service().ReapStaleAsync();

        _scriptCanceller.Verify(c => c.CancelAllAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Candidates_AreReadWithTheConfiguredStalenessAndBatchSize()
    {
        _leases.Setup(l => l.GetStaleAsync(It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await Service().ReapStaleAsync();

        _leases.Verify(l => l.GetStaleAsync(StaleAfter, ServerTaskLeaseOptions.ReaperBatchSize, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The reaper is always on: the DI constructor, given a setting with no configuration at all, still reads candidates with the default staleness.
    [Fact]
    public async Task DefaultSetting_ReapsWithFiveMinuteStaleness_NoDisablePath()
    {
        _leases.Setup(l => l.GetStaleAsync(It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var service = new ServerTaskLeaseReaperService(_leases.Object, _taskService.Object, _checkpoints.Object, _interruptions.Object, new Squid.Core.Settings.ServerTask.ServerTaskSetting());

        await service.ReapStaleAsync();

        _leases.Verify(l => l.GetStaleAsync(TimeSpan.FromSeconds(300), ServerTaskLeaseOptions.ReaperBatchSize, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handler_DispatchesToTheServiceOnce()
    {
        var service = new Mock<IServerTaskLeaseReaperService>();
        service.Setup(s => s.ReapStaleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
        var context = new Mock<Mediator.Net.Context.IReceiveContext<ReapStaleServerTasksCommand>>();
        context.SetupGet(c => c.Message).Returns(new ReapStaleServerTasksCommand());

        var response = await new ReapStaleServerTasksCommandHandler(service.Object).Handle(context.Object, CancellationToken.None);

        response.Data.Recovered.ShouldBe(3);
        service.Verify(s => s.ReapStaleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // The job id is Hangfire's key for the schedule: renaming it leaves the old schedule running beside the new one.
    [Fact]
    public async Task RecurringJob_IsAThinDispatcher_PinnedEveryMinute()
    {
        var mediator = new Mock<Mediator.Net.IMediator>();
        var job = new ServerTaskLeaseReaperRecurringJob(mediator.Object);

        await job.Execute();

        job.JobId.ShouldBe("server-task-lease-reaper");
        job.CronExpression.ShouldBe("* * * * *");
        mediator.Verify(m => m.SendAsync<ReapStaleServerTasksCommand, ReapStaleServerTasksResponse>(It.IsAny<ReapStaleServerTasksCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private void Stale(params StaleServerTask[] tasks)
        => _leases.Setup(l => l.GetStaleAsync(StaleAfter, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<StaleServerTask>(tasks));

    private void Recovers(StaleServerTask task, string to, int rows)
        => _leases.Setup(l => l.RecoverAsync(task, to, StaleAfter, It.IsAny<CancellationToken>())).ReturnsAsync(rows);

    private ServerTaskLeaseReaperService Service()
        => new(_leases.Object, _taskService.Object, _checkpoints.Object, _interruptions.Object, new ServerTaskLeaseOptions { Stale = StaleAfter }, _scriptCanceller.Object);
}
