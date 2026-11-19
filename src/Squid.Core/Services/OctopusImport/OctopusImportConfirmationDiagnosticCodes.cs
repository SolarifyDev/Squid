namespace Squid.Core.Services.OctopusImport;

public static class OctopusImportConfirmationDiagnosticCodes
{
    public const string ConfirmationAlreadyRunning = "OctopusImport.Confirmation.AlreadyRunning";
    public const string ConfirmationAlreadyCompleted = "OctopusImport.Confirmation.AlreadyCompleted";
    public const string ConfirmationRequiresValidatedSession = "OctopusImport.Confirmation.RequiresValidatedSession";

    /// <summary>The plan read for this confirmation was replaced (validated again, or the project selection changed): the session stays Validated.</summary>
    public const string ConfirmationSuperseded = "OctopusImport.Confirmation.Superseded";
    /// <summary>The confirmation imported nothing (rolled back, interrupted, or not started) and its session is Validated again: import again.</summary>
    public const string ConfirmationRetryable = "OctopusImport.Confirmation.Retryable";

    /// <summary>A transient database failure outlasted every automatic retry; nothing was imported.</summary>
    public const string TransientRetriesExhausted = "OctopusImport.Confirmation.TransientRetriesExhausted";

    /// <summary>The server running the import stopped before it committed; nothing was imported.</summary>
    public const string ConfirmationInterrupted = "OctopusImport.Confirmation.Interrupted";

    /// <summary>A transient database failure stopped a queued confirmation before it started; the session stays Validated.</summary>
    public const string ConfirmationNotStarted = "OctopusImport.Confirmation.NotStarted";

    public const string BackgroundConfirmationFailed = "OctopusImport.Confirmation.BackgroundFailed";
    public const string ValidationBlockedConfirmation = "OctopusImport.Confirmation.ValidationBlocked";
    public const string MappingBlockedConfirmation = "OctopusImport.Confirmation.MappingBlocked";
    public const string ResourceActionUnsupported = "OctopusImport.Confirmation.ResourceActionUnsupported";
    public const string ResourceTypeUnsupported = "OctopusImport.Confirmation.ResourceTypeUnsupported";
    public const string MissingPreviewResource = "OctopusImport.Confirmation.MissingPreviewResource";
    public const string MissingReuseDestination = "OctopusImport.Confirmation.MissingReuseDestination";
    public const string ResourceExecutionFailed = "OctopusImport.Confirmation.ResourceExecutionFailed";
    public const string TransactionRolledBack = "OctopusImport.Confirmation.TransactionRolledBack";
    public const string ChannelRulesStoredAsMetadata = "OctopusImport.Confirmation.ChannelRulesStoredAsMetadata";
    public const string DeploymentSettingsStoredAsProjectMetadata = "OctopusImport.Confirmation.DeploymentSettingsStoredAsProjectMetadata";
    public const string MissingReleasePackageFeedMapping = "OctopusImport.Confirmation.MissingReleasePackageFeedMapping";
    public const string TeamCreatedWithoutMembers = "OctopusImport.Confirmation.TeamCreatedWithoutMembers";
    public const string LibraryVariableSetVariablesMissing = "OctopusImport.Confirmation.LibraryVariableSetVariablesMissing";
    public const string ReusedLibraryVariableSetVariablesKept = "OctopusImport.Confirmation.ReusedLibraryVariableSetVariablesKept";
    public const string LibraryVariableSetCreatedConcurrently = "OctopusImport.Confirmation.LibraryVariableSetCreatedConcurrently";
}
