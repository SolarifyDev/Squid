using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>Where a reopened session continues, from what the session row holds; null when it cannot be continued.</summary>
public static class OctopusImportResumeSteps
{
    public static OctopusImportResumeStep? Of(OctopusImportSessionState state, OctopusImportSourceSummaryDto summary, bool hasUpload, bool hasConfirmationFailure)
        => state switch
        {
            OctopusImportSessionState.Uploaded => AfterUpload(summary, hasUpload),
            OctopusImportSessionState.Extracted => OctopusImportResumeStep.ContinueAfterRead,
            OctopusImportSessionState.Previewed => OctopusImportResumeStep.Preview,
            OctopusImportSessionState.Validated => hasConfirmationFailure ? OctopusImportResumeStep.RetryImport : OctopusImportResumeStep.Preview,
            OctopusImportSessionState.Importing => OctopusImportResumeStep.FollowImport,
            _ => null
        };

    public static OctopusImportSourceKind SourceKindOf(OctopusImportSourceSummaryDto summary)
        => IsServerSource(summary) ? OctopusImportSourceKind.OctopusServer : OctopusImportSourceKind.ExportFile;

    // A read from Octopus resumes until it is done; an export file whose upload was never registered has nothing to continue with.
    private static OctopusImportResumeStep? AfterUpload(OctopusImportSourceSummaryDto summary, bool hasUpload)
    {
        if (IsServerSource(summary)) return summary.HistoryFetch?.Done == true ? OctopusImportResumeStep.ContinueAfterRead : OctopusImportResumeStep.ResumeRead;

        return hasUpload ? OctopusImportResumeStep.ContinueAfterRead : null;
    }

    private static bool IsServerSource(OctopusImportSourceSummaryDto summary) => string.Equals(summary?.DetectedFormat, OctopusImportServerSourceService.ServerSourceFormat, StringComparison.Ordinal);
}
