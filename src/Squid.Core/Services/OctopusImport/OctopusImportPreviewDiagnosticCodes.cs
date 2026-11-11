namespace Squid.Core.Services.OctopusImport;

public static class OctopusImportPreviewDiagnosticCodes
{
    public const string DependencyPlanBlocker = "octopus.preview.dependency_plan_blocker";
    public const string ResourceOutOfScope = "octopus.preview.resource_out_of_scope";
    public const string ResourceUnsupported = "octopus.preview.resource_unsupported";
    public const string ReuseExistingResource = "octopus.preview.resource_reuse_existing";
    public const string RenameRequiredForProject = "octopus.preview.project_rename_required";
    public const string RenameRequiredForAmbiguousConflict = "octopus.preview.resource_rename_required_ambiguous";
    public const string ResourceBlockedByDependencyPlan = "octopus.preview.resource_blocked_by_dependency_plan";
    public const string RequiredSensitiveVariableInputMissing = "octopus.validation.required_sensitive_variable_input_missing";
    public const string UnresolvedReference = "octopus.validation.unresolved_reference";
    public const string MissingTargetRole = "octopus.validation.missing_target_role";
    public const string MissingMachine = "octopus.validation.missing_machine";
    public const string MissingAccount = "octopus.validation.missing_account";
    public const string MissingResponsibleTeam = "octopus.validation.missing_responsible_team";
    public const string IncompatibleSharedResourceReuse = "octopus.validation.incompatible_shared_resource_reuse";
    public const string StalePreviewPlan = "octopus.validation.stale_preview_plan";
    public const string SensitiveVariableValueDecrypted = "octopus.preview.sensitive_variable_value_decrypted";
    public const string ExportPasswordUnavailable = "octopus.preview.export_password_unavailable";
    public const string SensitiveResolutionChanged = "octopus.validation.sensitive_resolution_changed";

    public const string ExportPasswordUnavailableMessage = "The export password was accepted at upload but could not be kept or read back with the server's at-rest encryption key (Security:VariableEncryption:MasterKey is missing or has changed). Sensitive values are treated as if no export password had been given. Restore the original master key and retry, or configure a master key and upload the export again.";

    public const string ExportPasswordNotRetainedMessage = "The export password was accepted at upload but was not kept, because the server had no at-rest encryption key (Security:VariableEncryption:MasterKey) at that time. Sensitive values are treated as if no export password had been given. To import them, configure a master key and upload the export again.";

    public const string SensitiveResolutionChangedMessage = "This sensitive variable was unresolved when the preview plan was built but can now be decrypted with the export password. Refresh the preview before validating.";

    public const string SensitiveValueUnresolvedMessage = "The sensitive variable value could not be decrypted with the export password (it may be masked or encrypted with a different password). Confirmation is blocked while this required input is unresolved.";
}
