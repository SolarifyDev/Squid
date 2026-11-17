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
    public const string ReusedLibraryVariableKept = "octopus.preview.reused_library_variable_kept";
    public const string UnmappableVariableScope = "octopus.validation.unmappable_variable_scope";
    public const string ProjectVariableShadowedByLibraryVariable = "octopus.preview.project_variable_shadowed_by_library_variable";
    public const string ExportPasswordUnavailable = "octopus.preview.export_password_unavailable";
    public const string SensitiveResolutionChanged = "octopus.validation.sensitive_resolution_changed";
    public const string ChangedSinceLastImport = "octopus.preview.changed_since_last_import";
    public const string DefaultChannelNotRemapped = "octopus.preview.default_channel_not_remapped";
    public const string OctopusInstallationMismatch = "octopus.preview.octopus_installation_mismatch";
    public const string ImportOriginUnproven = "octopus.preview.import_origin_unproven";
    public const string ImportOriginPairingRequired = "octopus.preview.import_origin_pairing_required";
    public const string ImportOriginPairingUnconfirmed = "octopus.validation.import_origin_pairing_unconfirmed";
    public const string ReleaseVersionExistsInSquid = "octopus.preview.release_version_exists_in_squid";
    public const string HistoryOwnerNotImported = "octopus.preview.history_owner_not_imported";
    public const string DeploymentTaskMissing = "octopus.preview.deployment_task_missing";
    public const string TaskImportedWithDeployment = "octopus.preview.task_imported_with_deployment";
    public const string NotInEarlierImport = "octopus.preview.not_in_earlier_import";
    public const string ChildDeletedInSquid = "octopus.preview.child_deleted_in_squid";
    public const string ChildAddedInOctopus = "octopus.preview.child_added_in_octopus";
    public const string HistoryOwnerNotSelected = "octopus.preview.history_owner_not_selected";
    public const string ReleaseNotesEditedInSquid = "octopus.preview.release_notes_edited_in_squid";
    public const string HistoryRelinked = "octopus.preview.history_relinked";
    public const string OlderHistoryKept = "octopus.preview.older_history_kept";
    public const string UnreferencedResourceSkipped = "octopus.preview.unreferenced_resource_skipped";
    public const string UnreferencedResourcesSkipped = "octopus.preview.unreferenced_resources_skipped";
    public const string UnreferencedNameConflictSkipped = "octopus.preview.unreferenced_name_conflict_skipped";
    public const string SkippedWithOwner = "octopus.preview.skipped_with_owner";
    public const string UnsupportedResourcesDetected = "octopus.preview.unsupported_resources_detected";
    public const string BuiltInFeedExcluded = "octopus.preview.built_in_feed_excluded";
    public const string BuiltInFeedReferenced = "octopus.preview.built_in_feed_referenced";
    public const string SecretWithheldByServer = "octopus.preview.secret_withheld_by_server";
    public const string ProjectNotChosen = "octopus.preview.project_not_chosen";
    public const string NotUsedByChosenProjects = "octopus.preview.not_used_by_chosen_projects";
    public const string ResourcesNotUsedByChosenProjects = "octopus.preview.resources_not_used_by_chosen_projects";
    public const string ProjectsNotChosen = "octopus.preview.projects_not_chosen";
    public const string ConfigAsCodeNotSupported = "octopus.preview.config_as_code_not_supported";
    public const string ConfigAsCodeProjectsNotImported = "octopus.preview.config_as_code_projects_not_imported";
    public const string SecretsWithheldByServer = "octopus.preview.secrets_withheld_by_server";
    public const string SecretFillFromExport = "octopus.preview.secret_fill_from_export";
    public const string SecretSetInSquid = "octopus.preview.secret_set_in_squid";
    public const string SecretFillTargetGone = "octopus.preview.secret_fill_target_gone";
    public const string SecretFillAmbiguous = "octopus.preview.secret_fill_ambiguous";
    public const string SecretStillMissing = "octopus.preview.secret_still_missing";
    public const string SecretEmptyInOctopus = "octopus.preview.secret_empty_in_octopus";
    public const string SecretFillInstallationUnproven = "octopus.preview.secret_fill_installation_unproven";
    public const string SecretFillPairingRequired = "octopus.preview.secret_fill_pairing_required";
    public const string SecretFillPairingUnconfirmed = "octopus.validation.secret_fill_pairing_unconfirmed";
    public const string SecretFillNotBacked = "octopus.validation.secret_fill_not_backed";
    public const string SecretFillsSummary = "octopus.preview.secret_fills_summary";

    public const string ExportPasswordUnavailableMessage = "The export password was accepted at upload but could not be kept or read back with the server's at-rest encryption key (Security:VariableEncryption:MasterKey is missing or has changed). Sensitive values are treated as if no export password had been given. Restore the original master key and retry, or configure a master key and upload the export again.";

    public const string ExportPasswordNotRetainedMessage = "The export password was accepted at upload but was not kept, because the server had no at-rest encryption key (Security:VariableEncryption:MasterKey) at that time. Sensitive values are treated as if no export password had been given. To import them, configure a master key and upload the export again.";

    public const string SensitiveResolutionChangedMessage = "This sensitive variable was unresolved when the preview plan was built but can now be decrypted with the export password. Refresh the preview before validating.";

    public const string ReusedTeamGrantsApproval = "octopus.preview.reused_team_grants_approval";
    public const string BuiltInTeamWithoutEquivalent = "octopus.preview.built_in_team_without_equivalent";
    public const string BuiltInTeamEquivalentMissing = "octopus.preview.built_in_team_equivalent_missing";

    public const string VariableValueWithheldMessage = "The Octopus server API does not return sensitive values. This variable is imported with an empty value and listed as a missing secret; set it in Squid, or import the Octopus export file later to fill it. Deployments that use it fail until it is set.";

    public const string FeedPasswordWithheldMessage = "The Octopus server API does not return feed passwords. The feed is imported without credentials and listed as a missing secret; set them in Squid, or import the Octopus export file later to fill them.";

    public const string NotFillableSecretWithheldMessage = "The Octopus server API does not return this secret and the Octopus export file cannot supply it either. It is imported empty and listed as a missing secret; set it in Squid after the import.";

    public const string SensitiveValueUnresolvedMessage = "The sensitive variable value could not be decrypted with the export password (it may be masked or encrypted with a different password). Confirmation is blocked while this required input is unresolved.";

    public const string ProjectNotChosenMessage = "Not chosen for this import.";

    public const string NotUsedByChosenProjectsMessage = "Not used by the chosen projects, so it is not imported.";

    public const string HistoryOfProjectNotChosenMessage = "Belongs to the history of an Octopus project that was not chosen for this import.";

    public const string ConfigAsCodeProjectMessage = "This Octopus project is version-controlled (Config as Code): its process and settings live in a git repository, not in Octopus, so Squid does not import Config-as-Code projects.";
}
