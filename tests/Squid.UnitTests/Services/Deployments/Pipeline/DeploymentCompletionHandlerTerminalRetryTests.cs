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

    private DeploymentCompletionHandler Handler()
    {
        var setting = new ServerTaskSetting { TerminalWrite = new ServerTaskTerminalWriteSettings { MaxAttempts = Attempts, RetryDelayMilliseconds = 0 } };

        return new DeploymentCompletionHandler(_transactions, _serverTaskService.Object, _deploymentDataProvider.Object, _completionDataProvider.Object, Mock.Of<IAutoDeployService>(), _checkpointService.Object, null, setting);
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
