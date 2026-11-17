using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Live progress of the confirmation: the tracker is told where the import stands, and the progress run writes it on its own connection,
/// never inside the import transaction. One confirmation per scope, so the tracker and its run are fields, like a pipeline's context.
/// </summary>
public sealed partial class OctopusImportConfirmationOrchestrator
{
    private OctopusImportConfirmationProgressTracker _tracker;
    private IOctopusImportConfirmationProgressRun _progressRun;

    private IOctopusImportConfirmationProgressRun StartProgress(OctopusImportConfirmationRequest request)
    {
        _tracker = new OctopusImportConfirmationProgressTracker(request.AttemptId, request.DependencyPlan.OrderedResources, OctopusImportConfirmationProgressPlan.ProjectNames(request.Graph), _time);
        _progressRun = _progress?.Start(request.SessionId, _tracker) ?? NoProgress.Instance;

        return _progressRun;
    }

    // Returning to Validated releases the attempt, and every progress write is fenced on it: the rolled-back snapshot lands first.
    private async Task FlushRolledBackProgressAsync()
    {
        _tracker.Finish(OctopusImportConfirmationStage.RolledBack);

        await _progressRun.FlushAsync().ConfigureAwait(false);
    }

    private static OctopusImportConfirmationStage FinalStageOf(OctopusImportSessionState state) => state switch
    {
        OctopusImportSessionState.Succeeded => OctopusImportConfirmationStage.Succeeded,
        OctopusImportSessionState.Failed => OctopusImportConfirmationStage.Failed,
        _ => OctopusImportConfirmationStage.RolledBack
    };

    private sealed class NoProgress : IOctopusImportConfirmationProgressRun
    {
        public static readonly NoProgress Instance = new();

        public Task FlushAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
