using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>Which stored confirmation progress a session shows. Pure.</summary>
public static class OctopusImportConfirmationProgresses
{
    /// <summary>
    /// Whether progress written by <paramref name="progressAttemptId"/> describes the session as it is now: the attempt that holds or held
    /// it (Importing, Succeeded, Failed), or the attempt whose rollback left it Validated. Any other progress is a stale attempt's.
    /// </summary>
    public static bool Describes(OctopusImportSessionState state, Guid? confirmationAttemptId, Guid? lastFailureAttemptId, Guid progressAttemptId) => state switch
    {
        OctopusImportSessionState.Importing or OctopusImportSessionState.Succeeded or OctopusImportSessionState.Failed => confirmationAttemptId == progressAttemptId,
        OctopusImportSessionState.Validated => lastFailureAttemptId == progressAttemptId,
        _ => false
    };

    /// <summary>
    /// A Validated session's attempt is over even when its last write said otherwise (its server died and the recovery sweep returned the
    /// session): it is shown as rolled back, never as still running.
    /// </summary>
    public static OctopusImportConfirmationProgressDto Settled(OctopusImportConfirmationProgressDto progress, OctopusImportSessionState state)
    {
        if (state != OctopusImportSessionState.Validated || progress.Finished) return progress;

        progress.Stage = OctopusImportConfirmationStage.RolledBack;
        progress.Finished = true;
        progress.Current = null;

        return progress;
    }
}
