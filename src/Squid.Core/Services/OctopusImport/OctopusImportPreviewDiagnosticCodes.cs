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
}
