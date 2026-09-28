namespace Squid.Core.Services.OctopusImport.Mapping;

public static class OctopusImportDeploymentProcessMappingDiagnosticCodes
{
    public const string MissingProcessMapping = "OctopusImport.Process.MissingProcessMapping";
    public const string MissingEnvironmentMapping = "OctopusImport.Process.MissingEnvironmentMapping";
    public const string MissingExcludedEnvironmentMapping = "OctopusImport.Process.MissingExcludedEnvironmentMapping";
    public const string MissingChannelMapping = "OctopusImport.Process.MissingChannelMapping";
    public const string VariableScopedActionTargetUnsupported = "OctopusImport.Process.VariableScopedActionTargetUnsupported";
    public const string TenantTagsUnsupported = "OctopusImport.Process.TenantTagsUnsupported";
    public const string WorkerPoolUnsupported = "OctopusImport.Process.WorkerPoolUnsupported";
    public const string UnsupportedPackageRequirement = "OctopusImport.Process.UnsupportedPackageRequirement";
    public const string UnsupportedActionCondition = "OctopusImport.Process.UnsupportedActionCondition";
    public const string UnsupportedActionType = "OctopusImport.Process.UnsupportedActionType";
    public const string ActionPropertyMappingDeferred = "OctopusImport.Process.ActionPropertyMappingDeferred";
}
