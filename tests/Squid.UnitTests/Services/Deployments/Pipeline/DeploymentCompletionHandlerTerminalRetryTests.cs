using Npgsql;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.ServerTask.Exceptions;
using Squid.Core.Settings.ServerTask;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// A brief database blip while the terminal state and its DeploymentCompletion row are written (one transaction) must not turn a finished deployment into a
/// Failed one: the whole transaction is retried a bounded number of times on a transient database error. The data provider here fails AFTER the action ran
/// (as a lost commit does), so the rolled-back insert and the retry's re-insert are real. The retry is idempotent (the rolled-back row is detached, the
/// state write stays conditional on the lease) and never retries a refused lease or a permanent error.
/// </summary>
public class DeploymentCompletionHandlerTerminalRetryTests
{
    private const int Attempts = 3;

    private readonly FaultingTransactions _transactions = new();
    private readonly Mock<IServerTaskService> _serverTaskService = new();
    private readonly Mock<IDeploymentDataProvider> _deploymentDataProvider = new();
    private readonly Mock<IDeploymentCompletionDataProvider> _completionDataProvider = new();
    private readonly Mock<IDeploymentCheckpointService> _checkpointService = new();
    private readonly List<DeploymentCompletion> _added = new();

    public DeploymentCompletionHandlerTerminalRetryTests()
    {
        _deploymentDataProvider.Setup(d => d.GetDeploymentByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Deployment { Id = 1, SpaceId = 1 });
        _completionDataProvider.Setup(c => c.AddDeploymentCompletionAsync(It.IsAny<DeploymentCompletion>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Callback<DeploymentCompletion, bool, CancellationToken>((c, _, _) => _added.Add(c)).Returns(Task.CompletedTask);
        _serverTaskService.Setup(s => s.GetTaskAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new ServerTaskSummaryDto { Id = 1, State = TaskState.Executing });
    }

    [Fact]
    public async Task SuccessWhoseTransactionFailsOnceTransiently_IsRetried_AndEndsSuccessWithOneCompletion()
    {
        _transactions.FailTimes(1, Transient());

        await Handler().OnSuccessAsync(Ctx(), CancellationToken.None);

        _serverTaskService.Verify(s => s.TransitionStateAsync(1, TaskState.Executing, TaskState.Success, "owner", It.IsAny<CancellationToken>()), Times.Exactly(2));
        _transactions.Calls.ShouldBe(2);
        _added.Count.ShouldBe(2, "one insert per attempt");
        _completionDataProvider.Verify(c => c.Detach(_added[0]), Times.Once, "the rolled-back insert must not stay tracked");
        _completionDataProvider.Verify(c => c.Detach(_added[1]), Times.Never, "the committed insert is the one completion row");
        _checkpointService.Verify(c => c.DeleteAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailureOutcome_IsRetriedToo()
    {
        _transactions.FailTimes(2, Transient());

        await Handler().OnFailureAsync(Ctx(), new Exception("boom"), CancellationToken.None);

        _transactions.Calls.ShouldBe(3);
        _serverTaskService.Verify(s => s.TransitionStateAsync(1, TaskState.Executing, TaskState.Failed, "owner", It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task TransientFailureOnEveryAttempt_GivesUpAfterTheBound_AndThrowsAsToday()
    {
        _transactions.FailTimes(int.MaxValue, Transient());

        await Should.ThrowAsync<PostgresException>(() => Handler().OnSuccessAsync(Ctx(), CancellationToken.None));

        _transactions.Calls.ShouldBe(Attempts);
        _added.ForEach(c => _completionDataProvider.Verify(p => p.Detach(c), Times.Once));
        _checkpointService.Verify(c => c.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PermanentError_IsNotRetried()
    {
        _transactions.FailTimes(int.MaxValue, new PostgresException("check violated", "ERROR", "ERROR", "23514"));

        await Should.ThrowAsync<PostgresException>(() => Handler().OnSuccessAsync(Ctx(), CancellationToken.None));

        _transactions.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task ARefusedLease_IsNeverRetried_AndWritesNothingMore()
    {
        _serverTaskService.Setup(s => s.TransitionStateAsync(1, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new ServerTaskLeaseNotHeldException(1, "owner", "other"));

        await Handler().OnSuccessAsync(Ctx(), CancellationToken.None);

        _transactions.Calls.ShouldBe(1);
        _checkpointService.Verify(c => c.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ACancelledToken_StopsTheRetries()
    {
        using var cts = new CancellationTokenSource();
        _transactions.FailTimes(int.MaxValue, Transient());
        _transactions.OnFailure = cts.Cancel;

        await Should.ThrowAsync<PostgresException>(() => Handler().OnSuccessAsync(Ctx(), cts.Token));

        _transactions.Calls.ShouldBe(1);
    }

    // ── A retry after a commit whose result was unknown ─────────────────
    //
    // The first attempt's commit reached the server but its acknowledgement was lost (a dropped connection): the transaction failed transiently from this
    // run's point of view, yet the row is already in the target state with this run's completion record. The retry's conditional write then matches no row.
    // That refusal is the earlier attempt's success, not a failure: it is accepted exactly once, without a second completion record.

    private void TheFirstAttemptsCommitLanded(string target, string completionState)
    {
        _transactions.FailTimes(1, Transient());

        var calls = 0;
        _serverTaskService.Setup(s => s.TransitionStateAsync(1, It.IsAny<string>(), target, "owner", It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1 ? Task.CompletedTask : Task.FromException(new ServerTaskStateTransitionException(target, target)));

        var recorded = completionState == null ? new List<DeploymentCompletion>() : [new DeploymentCompletion { DeploymentId = 1, State = completionState }];
        _completionDataProvider.Setup(c => c.GetDeploymentCompletionsByDeploymentIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(recorded);
    }

    [Fact]
    public async Task Success_WhoseFirstCommitLandedUnacknowledged_IsAccepted_FinishesTheSuccess_WithOneCompletion()
    {
        var autoDeploy = new Mock<IAutoDeployService>();
        TheFirstAttemptsCommitLanded(TaskState.Success, TaskState.Success);

        (await Handler(autoDeploy.Object).OnSuccessAsync(Ctx(), CancellationToken.None)).ShouldBe(DeploymentOutcome.Succeeded);

        _added.Count.ShouldBe(1, "the retry is refused before it inserts: the earlier attempt's record is the one completion row");
        _checkpointService.Verify(c => c.DeleteAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        autoDeploy.Verify(a => a.TriggerAutoDeploymentsAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Cancelled)]
    public async Task FailureAndCancel_WhoseFirstCommitLandedUnacknowledged_AreAccepted(string target)
    {
        TheFirstAttemptsCommitLanded(target, TaskState.Failed);
        _serverTaskService.Setup(s => s.GetTaskAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new ServerTaskSummaryDto { Id = 1, State = target == TaskState.Cancelled ? TaskState.Cancelling : TaskState.Executing });

        var outcome = target == TaskState.Failed ? await Handler().OnFailureAsync(Ctx(), new Exception("boom"), CancellationToken.None) : await Handler().OnCancelledAsync(Ctx(), CancellationToken.None);

        outcome.ShouldBe(target == TaskState.Failed ? DeploymentOutcome.Failed : DeploymentOutcome.Cancelled);
        _added.Count.ShouldBe(1);
        _checkpointService.Verify(c => c.DeleteAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    // A pause writes no completion record: the row already Paused is the earlier attempt's write.
    [Fact]
    public async Task Pause_WhoseFirstCommitLandedUnacknowledged_IsAccepted()
    {
        TheFirstAttemptsCommitLanded(TaskState.Paused, null);

        (await Handler().OnTimedOutAsync(Ctx(), new Exception("timeout"), CancellationToken.None)).ShouldBe(DeploymentOutcome.Paused);
    }

    // The row is in the target state but this run's completion record is not there: someone else wrote it, so the refusal still surfaces.
    [Fact]
    public async Task TheTargetStateWithoutThisRunsCompletionRecord_IsNotAccepted()
    {
        TheFirstAttemptsCommitLanded(TaskState.Success, null);

        await Should.ThrowAsync<ServerTaskStateTransitionException>(() => Handler().OnSuccessAsync(Ctx(), CancellationToken.None));

        _checkpointService.Verify(c => c.DeleteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Only a retry can be answering its own earlier commit: a first attempt refused because the row is already in the target state is not this run's write.
    [Fact]
    public async Task AFirstAttemptFindingTheTargetStateAlready_IsNotAccepted()
    {
        _serverTaskService.Setup(s => s.TransitionStateAsync(1, TaskState.Executing, TaskState.Success, "owner", It.IsAny<CancellationToken>())).ThrowsAsync(new ServerTaskStateTransitionException(TaskState.Success, TaskState.Success));
        _completionDataProvider.Setup(c => c.GetDeploymentCompletionsByDeploymentIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([new DeploymentCompletion { DeploymentId = 1, State = TaskState.Success }]);

        await Should.ThrowAsync<ServerTaskStateTransitionException>(() => Handler().OnSuccessAsync(Ctx(), CancellationToken.None));

        _transactions.Calls.ShouldBe(1);
    }

    // ── The retry wait is bounded ───────────────────────────────────────

    // A large attempt bound used to grow the wait without limit (200 ms × 3^(n−1)) and, from the 16th attempt, overflow Task.Delay so an
    // ArgumentOutOfRangeException replaced the real error. The total wait budget now ends the retries and the real failure surfaces.
    [Fact]
    public async Task ALargeAttemptBound_IsEndedByTheTotalWaitBudget_AndTheRealErrorSurfaces()
    {
        _transactions.FailTimes(int.MaxValue, Transient());
        var setting = new ServerTaskSetting { TerminalWrite = new ServerTaskTerminalWriteSettings { MaxAttempts = 1000, RetryDelayMilliseconds = 1, MaxRetryDelayMilliseconds = 2, MaxTotalRetryDelayMilliseconds = 10 } };

        await Should.ThrowAsync<PostgresException>(() => Handler(setting: setting).OnSuccessAsync(Ctx(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));

        _transactions.Calls.ShouldBeLessThan(20, "the 10 ms budget allows a handful of 1–2 ms waits, not 1000 attempts");
    }

    private DeploymentCompletionHandler Handler(IAutoDeployService autoDeploy = null, ServerTaskSetting setting = null)
    {
        setting ??= new ServerTaskSetting { TerminalWrite = new ServerTaskTerminalWriteSettings { MaxAttempts = Attempts, RetryDelayMilliseconds = 0 } };

        return new DeploymentCompletionHandler(_transactions, _serverTaskService.Object, _deploymentDataProvider.Object, _completionDataProvider.Object, autoDeploy ?? Mock.Of<IAutoDeployService>(), _checkpointService.Object, null, setting);
    }

    private static DeploymentTaskContext Ctx() => new() { ServerTaskId = 1, Deployment = new Deployment { Id = 1 }, LeaseOwner = "owner" };

    private static PostgresException Transient() => new("serialization failure", "ERROR", "ERROR", "40001");

    // Runs the action, then fails like a lost commit would: the work was done in memory but the transaction did not land.
    private sealed class FaultingTransactions : IGenericDataProvider
    {
        private int _failuresLeft;
        private Exception _failure;

        public int Calls { get; private set; }

        public Action OnFailure { get; set; }

        public void FailTimes(int times, Exception failure)
        {
            _failuresLeft = times;
            _failure = failure;
        }

        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
        {
            Calls++;

            var result = await action(cancellationToken);

            Fail();

            return result;
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            Calls++;

            await action(cancellationToken);

            Fail();
        }

        private void Fail()
        {
            if (_failuresLeft <= 0) return;

            _failuresLeft--;
            OnFailure?.Invoke();

            throw _failure;
        }
    }
}
