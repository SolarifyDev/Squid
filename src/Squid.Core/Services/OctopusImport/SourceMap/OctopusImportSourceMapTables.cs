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
        [OctopusImportSourceMapQueries.InstallationSourceType] = "space",
        [OctopusImportSourceMapQueries.ExportSourceSourceType] = "space",
        [OctopusImportSourceMapQueries.VariableSecretSourceType] = "variable_set",
        [OctopusImportSourceMapQueries.FeedSecretSourceType] = "external_feed"
    };

    /// <summary>
    /// What else must exist for a row of an owned kind to be live, as a predicate over its destination row <c>d</c>.
    /// Squid deletes do not cascade: a deleted project leaves its channels, variable set, process and history behind,
    /// and a row pointing at such an orphan must be stale, or the re-created owner would be left empty.
    /// </summary>
    public static IReadOnlyDictionary<string, string> OwnerPredicates { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(OctopusResourceKind.LifecyclePhase)] = "EXISTS (SELECT 1 FROM lifecycle ol WHERE ol.id = d.lifecycle_id)",
        [nameof(OctopusResourceKind.Channel)] = ProjectExists("d.project_id"),
        [nameof(OctopusResourceKind.VariableSet)] = VariableSetOwned("d"),
        [OctopusImportSourceMapQueries.VariableSecretSourceType] = VariableSetOwned("d"),
        [nameof(OctopusResourceKind.Variable)] = $"EXISTS (SELECT 1 FROM variable_set vs WHERE vs.id = d.variable_set_id AND {VariableSetOwned("vs")})",
        [nameof(OctopusResourceKind.DeploymentProcess)] = ProcessOwned("d"),
        [nameof(OctopusResourceKind.DeploymentStep)] = $"EXISTS (SELECT 1 FROM deployment_process dp WHERE dp.id = d.process_id AND {ProcessOwned("dp")})",
        [nameof(OctopusResourceKind.DeploymentAction)] = $"EXISTS (SELECT 1 FROM deployment_step ds JOIN deployment_process dp ON dp.id = ds.process_id WHERE ds.id = d.step_id AND {ProcessOwned("dp")})",
        [nameof(OctopusResourceKind.Release)] = $"{ProjectExists("d.project_id")} AND {ChannelExists("d.channel_id")}",
        [nameof(OctopusResourceKind.Deployment)] = $"{ProjectExists("d.project_id")} AND {ChannelExists("d.channel_id")} AND EXISTS (SELECT 1 FROM release orl WHERE orl.id = d.release_id) AND EXISTS (SELECT 1 FROM environment oe WHERE oe.id = d.environment_id)"
    };

    private static string ProjectExists(string column) => $"EXISTS (SELECT 1 FROM project op WHERE op.id = {column})";

    private static string ChannelExists(string column) => $"EXISTS (SELECT 1 FROM channel oc WHERE oc.id = {column})";

    // Owned through the owner's pointer, not the set's own owner column: a re-created project points at its new set.
    private static string VariableSetOwned(string alias)
        => $"(EXISTS (SELECT 1 FROM project op WHERE op.variable_set_id = {alias}.id) OR EXISTS (SELECT 1 FROM library_variable_set olv WHERE olv.variable_set_id = {alias}.id))";

    private static string ProcessOwned(string alias) => $"EXISTS (SELECT 1 FROM project op WHERE op.deployment_process_id = {alias}.id)";
}
