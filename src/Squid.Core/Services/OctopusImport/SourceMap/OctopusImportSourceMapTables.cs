using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.SourceMap;

/// <summary>
/// The destination table of every source type the import records, so the loader can drop rows whose
/// destination was deleted. A type missing here is never trusted (its rows are treated as stale).
/// Table names are constants, never user input, so they are safe to splice into SQL.
/// </summary>
public static class OctopusImportSourceMapTables
{
    public static IReadOnlyDictionary<string, string> ByKind { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(OctopusResourceKind.ProjectGroup)] = "project_group",
        [nameof(OctopusResourceKind.Environment)] = "environment",
        [nameof(OctopusResourceKind.Lifecycle)] = "lifecycle",
        [nameof(OctopusResourceKind.LifecyclePhase)] = "phase",
        [nameof(OctopusResourceKind.Feed)] = "external_feed",
        [nameof(OctopusResourceKind.Account)] = "deployment_account",
        [nameof(OctopusResourceKind.Team)] = "team",
        [nameof(OctopusResourceKind.Machine)] = "machine",
        [nameof(OctopusResourceKind.LibraryVariableSet)] = "library_variable_set",
        [nameof(OctopusResourceKind.Project)] = "project",
        [nameof(OctopusResourceKind.Channel)] = "channel",
        [nameof(OctopusResourceKind.VariableSet)] = "variable_set",
        [nameof(OctopusResourceKind.Variable)] = "variable",
        [nameof(OctopusResourceKind.DeploymentProcess)] = "deployment_process",
        [nameof(OctopusResourceKind.DeploymentStep)] = "deployment_step",
        [nameof(OctopusResourceKind.DeploymentAction)] = "deployment_action",
        [nameof(OctopusResourceKind.Release)] = "release",
        [nameof(OctopusResourceKind.Deployment)] = "deployment",
        [nameof(OctopusResourceKind.ServerTask)] = "server_task",
        [OctopusImportSourceMapQueries.InstallationSourceType] = "space"
    };
}
