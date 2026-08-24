namespace Squid.Core.Services.OctopusImport;

public static class OctopusImportConfirmationDiagnosticCodes
{
    public const string ConfirmationAlreadyRunning = "OctopusImport.Confirmation.AlreadyRunning";
    public const string ConfirmationAlreadyCompleted = "OctopusImport.Confirmation.AlreadyCompleted";
    public const string ConfirmationRequiresValidatedSession = "OctopusImport.Confirmation.RequiresValidatedSession";
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
}
