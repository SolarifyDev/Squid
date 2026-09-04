namespace Squid.Core.Services.OctopusImport.Mapping;

public static class OctopusImportProjectMappingDiagnosticCodes
{
    public const string MissingProjectGroupMapping = "octopus.mapping.project.missing_project_group_mapping";
    public const string MissingLifecycleMapping = "octopus.mapping.project.missing_lifecycle_mapping";
    public const string IncludedLibraryVariableSetsDeferred = "octopus.mapping.project.included_library_variable_sets_deferred";
    public const string ChannelDefaultsStoredAsMetadata = "octopus.mapping.project.channel_defaults_stored_as_metadata";
    public const string DeploymentSettingsStoredAsMetadata = "octopus.mapping.project.deployment_settings_stored_as_metadata";
    public const string UnsupportedTenantedDeploymentMode = "octopus.mapping.project.unsupported_tenanted_deployment_mode";
}
