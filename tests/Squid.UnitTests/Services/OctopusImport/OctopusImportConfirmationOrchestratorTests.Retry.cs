using System.Linq;
using Npgsql;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// An attempt that does not commit wrote nothing: a transient database failure is retried in place, any other failure returns the
/// session to Validated with the reason (import again, no re-read), and only a plan that cannot be imported as it is ends Failed.
/// </summary>
public partial class OctopusImportConfirmationOrchestratorTests
{
    [Fact]
    public async Task TransientThenSuccess_RetriesOnceAndSucceeds()
    {
        var time = new ImmediateTime();
        var harness = CreateMinimalHarness(time: time);
        harness.TransactionExecutor.Failures.Enqueue(Postgres("40001"));

        var session = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        harness.TransactionExecutor.ExecuteCalls.ShouldBe(2);
        harness.SessionService.ReturnToValidatedCalls.ShouldBe(0);
        time.Waits.ShouldHaveSingleItem().TotalMilliseconds.ShouldBeInRange(1600, 2400, "the first backoff is 2s ±20%, waited on the injected clock");
    }

    [Fact]
    public async Task TransientExhausted_ReturnsValidatedWithFailure()
    {
        var time = new ImmediateTime();
        var harness = CreateMinimalHarness(time: time);
        for (var i = 0; i < 3; i++) harness.TransactionExecutor.Failures.Enqueue(Postgres("40P01"));

        var session = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        session.State.ShouldBe(OctopusImportSessionState.Validated);
        harness.TransactionExecutor.ExecuteCalls.ShouldBe(3);
        time.Waits.Count.ShouldBe(2);
        time.Waits[1].TotalMilliseconds.ShouldBeInRange(6400, 9600);
        var failure = harness.SessionService.ReturnedFailure.ShouldNotBeNull();
        failure.ShouldSatisfyAllConditions(
            f => f.Reason.ShouldBe(OctopusImportConfirmationFailureReason.TransientRetriesExhausted),
            f => f.Transient.ShouldBeTrue(),
            f => f.Attempts.ShouldBe(3),
            f => f.AttemptId.ShouldBe(harness.Request.AttemptId),
            f => f.Diagnostics.First().Code.ShouldBe(OctopusImportConfirmationDiagnosticCodes.TransientRetriesExhausted),
            f => f.Diagnostics.First().Message.ShouldContain("40P01"));
        harness.SessionService.RecordResultCalls.ShouldBe(3, "each attempt's in-transaction Succeeded record rolled back; no Failed record follows");
    }

    [Fact]
    public async Task NonTransientRollback_ReturnsValidatedWithoutRetry()
    {
        var time = new ImmediateTime();
        var harness = CreateMinimalHarness(throwAfterAction: true, time: time);

        var session = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        session.State.ShouldBe(OctopusImportSessionState.Validated);
        session.LastConfirmationFailure.ShouldNotBeNull().Reason.ShouldBe(OctopusImportConfirmationFailureReason.RolledBack);
        harness.TransactionExecutor.ExecuteCalls.ShouldBe(1);
        time.Waits.ShouldBeEmpty();
        harness.SessionService.ReturnedFailure.ShouldSatisfyAllConditions(f => f.Attempts.ShouldBe(1), f => f.Transient.ShouldBeFalse(), f => f.Diagnostics.ShouldContain(d => d.Code == OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack));
    }

    // A plan that is stale against the destination fails the same way every time: the session ends Failed, it is not offered again.
    [Fact]
    public async Task PlanFailure_EndsFailed_NotRetryable()
    {
        var harness = CreateMinimalHarness(throwAfterAction: true);
        harness.TransactionExecutor.AfterActionException = new OctopusImportStalePlanException("Project", "Projects-1");

        var session = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        session.State.ShouldBe(OctopusImportSessionState.Failed);
        harness.SessionService.ReturnToValidatedCalls.ShouldBe(0);
        harness.TransactionExecutor.ExecuteCalls.ShouldBe(1);
    }

    [Fact]
    public async Task RevalidationBlockers_StillRecordFailed()
    {
        var harness = CreateMinimalHarness();
        harness.PreviewValidator.Setup(v => v.Validate(It.IsAny<OctopusResourceGraph>(), It.IsAny<OctopusImportDependencyPlan>(), It.IsAny<OctopusImportConflictDiscoveryResult>(), It.IsAny<OctopusImportPreviewPlanDto>())).Returns(new OctopusImportValidationResultDto { Diagnostics = [new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = "octopus.validation.stale_preview_plan", Message = "changed" }] });

        var session = await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        session.State.ShouldBe(OctopusImportSessionState.Failed);
        harness.TransactionExecutor.ExecuteCalls.ShouldBe(0);
        harness.SessionService.ReturnToValidatedCalls.ShouldBe(0);
    }

    // The retried attempt starts from the validated plan again: nothing the rolled-back attempt marked carries over into the result.
    [Fact]
    public async Task EachAttemptStartsFromFreshResult()
    {
        var clean = CreateMinimalHarness(includeReusedEnvironment: true);
        await clean.Sut.ConfirmAsync(clean.Request, CancellationToken.None);

        var retried = CreateMinimalHarness(includeReusedEnvironment: true);
        retried.TransactionExecutor.Failures.Enqueue(Postgres("40001"));
        var session = await retried.Sut.ConfirmAsync(retried.Request, CancellationToken.None);

        session.State.ShouldBe(OctopusImportSessionState.Succeeded);
        var recorded = retried.SessionService.RecordedResult;
        recorded.Diagnostics.ShouldNotContain(d => d.Code == OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack);
        recorded.Resources.ShouldNotContain(r => r.OutcomeState == OctopusImportResourceOutcomeState.Failed);
        recorded.IdMappings.Count.ShouldBe(clean.SessionService.RecordedResult.IdMappings.Count);
        recorded.Diagnostics.Count.ShouldBe(clean.SessionService.RecordedResult.Diagnostics.Count);
        recorded.Resources.Select(r => (r.SourceId, r.OutcomeState)).ShouldBe(clean.SessionService.RecordedResult.Resources.Select(r => (r.SourceId, r.OutcomeState)));
    }

    // The commit's acknowledgement was lost but it landed: the attempt no longer holds the session, so it is reported, not retried.
    [Fact]
    public async Task CommitOutcomeUnknown_SessionNoLongerHeld_DoesNotRetry()
    {
        var harness = CreateMinimalHarness();
        harness.TransactionExecutor.Failures.Enqueue(Postgres("08006"));
        harness.SessionService.HeldOverride = false;

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        harness.TransactionExecutor.ExecuteCalls.ShouldBe(1);
        harness.SessionService.ReturnToValidatedCalls.ShouldBe(0);
    }

    // The recovery sweep returned the session while this attempt ran: its settle is refused, its transaction rolls back, and the
    // session is reported as the sweep left it.
    [Fact]
    public async Task FencedOut_ReturnsCurrentSessionWithoutReturningToValidated()
    {
        var harness = CreateMinimalHarness(withTransactionGate: true);

        var confirm = harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);
        await harness.TransactionExecutor.Entered.Task;
        harness.SessionService.MoveOn(OctopusImportSessionState.Validated);
        harness.TransactionExecutor.Release.SetResult(true);
        var session = await confirm;

        session.State.ShouldBe(OctopusImportSessionState.Validated);
        harness.SessionService.ReturnToValidatedCalls.ShouldBe(0);
        harness.SessionService.RecordResultCalls.ShouldBe(0, "a fenced-out attempt never records a result");
    }

    [Theory]
    [InlineData("success", 1)]
    [InlineData("rollback", 1)]
    [InlineData("blockers", 1)]
    [InlineData("not-admitted", 0)]
    public async Task HeartbeatStartedAfterAdmissionAndDisposedOnEveryPath(string path, int expectedBeats)
    {
        var heartbeat = new RecordingHeartbeat();
        var harness = CreateMinimalHarness(admissionShouldSucceed: path != "not-admitted", throwAfterAction: path == "rollback", heartbeat: heartbeat);
        if (path == "blockers") harness.Request.PreviewPlan.Diagnostics.Add(new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = "blocked", Message = "blocked" });

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        heartbeat.Started.Count.ShouldBe(expectedBeats);
        heartbeat.Disposed.ShouldBe(expectedBeats);
        if (expectedBeats == 1) heartbeat.Started.Single().ShouldBe((harness.Request.SessionId, harness.Request.AttemptId));
    }

    // A driver's message can quote connection settings: only the exception type and SQLSTATE reach the stored failure.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureMessage_NeverContainsExceptionMessage(bool transient)
    {
        var harness = CreateMinimalHarness(throwAfterAction: true);
        var secret = "replica-" + Guid.NewGuid().ToString("N"); // nothing the redaction would recognise: only never using the message keeps it out
        harness.TransactionExecutor.AfterActionException = transient ? new PostgresException(secret, "ERROR", "ERROR", "40001") : new InvalidOperationException(secret);
        harness.TransactionExecutor.ThrowAfterAction = true;

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        var failure = harness.SessionService.ReturnedFailure.ShouldNotBeNull();
        failure.Diagnostics.ShouldNotBeEmpty();
        failure.Diagnostics.ShouldAllBe(d => !d.Message.Contains(secret));
    }

    [Fact]
    public async Task ImportTransaction_EndsItselfWhenItsHolderDies_AfterTheStaleThreshold()
    {
        var harness = CreateMinimalHarness();

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        harness.TransactionExecutor.Contexts.ShouldHaveSingleItem().IdleInTransactionTimeout.ShouldBe(TimeSpan.FromSeconds(OctopusImportConfirmationResilienceOptions.DefaultHeartbeatStaleSeconds));
    }

    // A large plan stores a small failure, the root cause first: blockers ahead of warnings, the rollback consequence after them.
    [Fact]
    public async Task RetainedDiagnostics_BlockersFirst_CappedAt20()
    {
        var harness = CreateMinimalHarness(throwAfterAction: true);
        for (var i = 0; i < 30; i++) harness.Request.PreviewPlan.Diagnostics.Add(new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Warning, Code = $"warning.{i}", Message = "warning" });

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        var diagnostics = harness.SessionService.ReturnedFailure.Diagnostics;
        diagnostics.Count.ShouldBe(OctopusImportConfirmationOrchestrator.MaxRetainedFailureDiagnostics);
        diagnostics.First().Code.ShouldBe(OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack);
    }

    private static PostgresException Postgres(string sqlState) => new("simulated failure", "ERROR", "ERROR", sqlState);

    private sealed class RecordingHeartbeat : IOctopusImportConfirmationHeartbeat
    {
        public List<(Guid SessionId, Guid AttemptId)> Started { get; } = [];

        public int Disposed { get; private set; }

        public IAsyncDisposable Start(Guid sessionId, Guid attemptId)
        {
            Started.Add((sessionId, attemptId));

            return new Handle(this);
        }

        private sealed class Handle(RecordingHeartbeat owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.Disposed++;

                return ValueTask.CompletedTask;
            }
        }
    }
}
