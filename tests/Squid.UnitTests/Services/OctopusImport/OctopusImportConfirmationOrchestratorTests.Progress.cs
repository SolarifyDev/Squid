using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// Live progress of the confirmation: the stages it passes through, every planned item counted once per attempt, a retry counting again
/// from zero, and the rolled-back snapshot written before the session (and with it the attempt every progress write is fenced on) is released.
/// </summary>
public partial class OctopusImportConfirmationOrchestratorTests
{
    [Fact]
    public async Task Progress_Success_PassesEveryStageInOrder_AndCountsEveryItem()
    {
        var progress = new RecordingProgress();
        var harness = CreateMinimalHarness(progress: progress);

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        var final = progress.Final.ShouldNotBeNull();
        final.ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.Succeeded), p => p.Finished.ShouldBeTrue(), p => p.AttemptId.ShouldBe(harness.Request.AttemptId), p => p.ItemsTotal.ShouldBeGreaterThan(0), p => p.ItemsDone.ShouldBe(p.ItemsTotal), p => p.Current.ShouldBeNull());
        final.Phases.ShouldAllBe(p => p.Done == p.Total);
        progress.Started.ShouldHaveSingleItem().ShouldBe(harness.Request.SessionId);
        progress.Tracker.ShouldNotBeNull().Snapshot().Progress.RecentLog.Count.ShouldBeLessThanOrEqualTo(OctopusImportConfirmationProgressTracker.MaxRecentLogEntries);
        StagesOf(final).ShouldBe([OctopusImportConfirmationStage.Validating, OctopusImportConfirmationStage.WaitingForSpaceLock, OctopusImportConfirmationStage.Importing, OctopusImportConfirmationStage.FillingSecrets, OctopusImportConfirmationStage.RecordingSourceMap, OctopusImportConfirmationStage.Committing, OctopusImportConfirmationStage.Succeeded]);
    }

    // An item an earlier pass already completed is not imported again, but it is counted: otherwise the run never reaches its total.
    [Fact]
    public async Task Progress_AlreadyCompletedItems_StillCounted()
    {
        var progress = new RecordingProgress();
        var harness = CreateMinimalHarness(progress: progress);
        harness.Request.PreviewPlan.Resources.First(r => r.SourceType == "Environment").OutcomeState = OctopusImportResourceOutcomeState.Skipped;

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        progress.Final.ShouldNotBeNull().ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.Succeeded), p => p.ItemsDone.ShouldBe(p.ItemsTotal));
        progress.Final.RecentLog.ShouldContain(e => e.Kind == "Environment" && e.Outcome == OctopusImportResourceOutcomeState.Skipped);
    }

    // The transient failure struck after the attempt's work: the retry imports the whole plan again, so it counts again from zero.
    [Fact]
    public async Task Progress_TransientRetry_CountsAgainFromZero()
    {
        var progress = new RecordingProgress();
        var harness = CreateMinimalHarness(progress: progress);
        harness.TransactionExecutor.Failures.Enqueue(Postgres("40001"));

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        progress.Final.ShouldNotBeNull().ShouldSatisfyAllConditions(p => p.Attempt.ShouldBe(2), p => p.Stage.ShouldBe(OctopusImportConfirmationStage.Succeeded), p => p.ItemsDone.ShouldBe(p.ItemsTotal));
    }

    // Returning to Validated releases the attempt, and every progress write is fenced on it: the rolled-back snapshot must land first.
    [Fact]
    public async Task Progress_Rollback_FlushedBeforeTheSessionIsReturnedToValidated()
    {
        var progress = new RecordingProgress();
        var harness = CreateMinimalHarness(throwAfterAction: true, progress: progress);
        progress.Probe = () => harness.SessionService.ReturnToValidatedCalls;

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        harness.SessionService.ReturnToValidatedCalls.ShouldBe(1);
        progress.Flushes.ShouldContain(f => f.Stage == OctopusImportConfirmationStage.RolledBack && f.ReturnToValidatedCallsBefore == 0, "the rolled-back progress was flushed only after the attempt was released, so the fenced write could never land");
        progress.Final.ShouldNotBeNull().ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.RolledBack), p => p.Finished.ShouldBeTrue());
    }

    [Fact]
    public async Task Progress_BlockerSettle_EndsFailed()
    {
        var progress = new RecordingProgress();
        var harness = CreateMinimalHarness(progress: progress);
        harness.Request.PreviewPlan.Diagnostics.Add(new OctopusImportDiagnosticDto { Severity = OctopusImportCompatibilitySeverity.Blocker, Code = "blocked", Message = "blocked" });

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        progress.Final.ShouldNotBeNull().ShouldSatisfyAllConditions(p => p.Stage.ShouldBe(OctopusImportConfirmationStage.Failed), p => p.Finished.ShouldBeTrue(), p => p.ItemsDone.ShouldBe(0));
    }

    [Fact]
    public async Task Progress_NotAdmitted_NeverStarted()
    {
        var progress = new RecordingProgress();
        var harness = CreateMinimalHarness(admissionShouldSucceed: false, progress: progress);

        await harness.Sut.ConfirmAsync(harness.Request, CancellationToken.None);

        progress.Started.ShouldBeEmpty();
    }

    private static List<OctopusImportConfirmationStage> StagesOf(OctopusImportConfirmationProgressDto progress) => progress.RecentLog.Where(e => e.Kind == null).Select(e => e.Stage).ToList();

    private sealed class RecordingProgress : IOctopusImportConfirmationProgress
    {
        public List<Guid> Started { get; } = [];

        public OctopusImportConfirmationProgressTracker Tracker { get; private set; }

        public List<(OctopusImportConfirmationStage Stage, int ReturnToValidatedCallsBefore)> Flushes { get; } = [];

        public OctopusImportConfirmationProgressDto Final { get; private set; }

        public Func<int> Probe { get; set; } = () => 0;

        public IOctopusImportConfirmationProgressRun Start(Guid sessionId, OctopusImportConfirmationProgressTracker tracker)
        {
            Started.Add(sessionId);
            Tracker = tracker;

            return new Run(this);
        }

        private sealed class Run(RecordingProgress owner) : IOctopusImportConfirmationProgressRun
        {
            public Task FlushAsync()
            {
                owner.Flushes.Add((owner.Tracker.Snapshot().Progress.Stage, owner.Probe()));

                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                owner.Final = owner.Tracker.Snapshot().Progress;

                return ValueTask.CompletedTask;
            }
        }
    }
}
