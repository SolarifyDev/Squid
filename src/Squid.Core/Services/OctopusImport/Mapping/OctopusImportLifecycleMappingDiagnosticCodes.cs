namespace Squid.Core.Services.OctopusImport.Mapping;

public static class OctopusImportLifecycleMappingDiagnosticCodes
{
    public const string MissingAutomaticDeploymentEnvironmentMapping = "octopus.mapping.lifecycle.missing_automatic_deployment_environment_mapping";
    public const string MissingOptionalDeploymentEnvironmentMapping = "octopus.mapping.lifecycle.missing_optional_deployment_environment_mapping";
    public const string UnsupportedLifecycleRetentionPolicy = "octopus.mapping.lifecycle.unsupported_lifecycle_retention_policy";
    public const string UnsupportedPhaseRetentionPolicy = "octopus.mapping.lifecycle.unsupported_phase_retention_policy";
}
