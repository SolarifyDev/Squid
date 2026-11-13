using System.Text.Json.Nodes;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher
{
    public const string ResponsibleTeamIdsProperty = "Octopus.Action.Manual.ResponsibleTeamIds";

    // ── Project configuration ──────────────────────────────────────────────

    // The project's own documents are one item (a list Octopus does not have, e.g. runbooks on an older server, is empty): all of them are read before the first is written, so an item abandoned
    // while reading leaves nothing of itself staged. The project document itself was staged by the catalog.
    private async Task FetchProjectConfigurationAsync(CancellationToken ct)
    {
        _ctx.InItem = true;

        var configuration = await ReadProjectConfigurationAsync(CurrentProjectId, ct).ConfigureAwait(false);

        await StageProjectConfigurationAsync(configuration, ct).ConfigureAwait(false);

        CommitProjectConfiguration();
    }

    private async Task<ProjectConfigurationDocuments> ReadProjectConfigurationAsync(string projectId, CancellationToken ct)
    {
        var process = await GetOrNullAsync<JsonObject>(ProjectChildPath(projectId, "deploymentprocesses"), ct).ConfigureAwait(false);
        var settings = await DeploymentSettingsAsync(projectId, ct).ConfigureAwait(false);
        var variables = await GetOrNullAsync<JsonObject>(ProjectChildPath(projectId, "variables"), ct).ConfigureAwait(false);
        var channels = await GetAllPagesOrEmptyAsync<JsonObject>(ProjectChildPath(projectId, "channels"), ct, tolerateNotFound: true).ConfigureAwait(false);
        var runbooks = await GetAllPagesOrEmptyAsync<JsonObject>(ProjectChildPath(projectId, "runbooks"), ct, tolerateNotFound: true).ConfigureAwait(false);
        var triggers = await GetAllPagesOrEmptyAsync<JsonObject>(ProjectChildPath(projectId, "triggers"), ct, tolerateNotFound: true).ConfigureAwait(false);

        return new ProjectConfigurationDocuments { ProjectId = projectId, Process = process, Settings = settings, Variables = variables, Channels = channels, Runbooks = runbooks, Triggers = triggers };
    }

    // A server without the deployment settings endpoint (404) keeps the settings on the project document, which the
    // normaliser reshapes into the settings document; a refused request is recorded and stays refused.
    private async Task<JsonObject> DeploymentSettingsAsync(string projectId, CancellationToken ct)
    {
        var rejectedBefore = _ctx.Pending.ItemsRejected;
        var settings = await GetOrNullAsync<JsonObject>(ProjectChildPath(projectId, "deploymentsettings"), ct).ConfigureAwait(false);

        if (settings != null || _ctx.Pending.ItemsRejected != rejectedBefore) return settings;

        return await bundleWriter.ReadStagedAsync(_ctx.Context.SessionDirectory, OctopusResourceKind.Project, projectId, ct).ConfigureAwait(false);
    }

    private async Task StageProjectConfigurationAsync(ProjectConfigurationDocuments configuration, CancellationToken ct)
    {
        var projectId = configuration.ProjectId;
        var process = await StageAsync(OctopusResourceKind.DeploymentProcess, configuration.Process, projectId, ct).ConfigureAwait(false);

        RecordProcessReferences(process);

        await StageAsync(OctopusResourceKind.DeploymentSettings, configuration.Settings, projectId, ct).ConfigureAwait(false);
        await StageAsync(OctopusResourceKind.VariableSet, configuration.Variables, projectId, ct).ConfigureAwait(false);
        await StageAllAsync(OctopusResourceKind.Channel, configuration.Channels, projectId, ct).ConfigureAwait(false);
        await StageAllAsync(OctopusResourceKind.Runbook, configuration.Runbooks, projectId, ct).ConfigureAwait(false);
        await StageAllAsync(OctopusResourceKind.Trigger, configuration.Triggers, projectId, ct).ConfigureAwait(false);
    }

    private async Task StageAllAsync(OctopusResourceKind kind, IEnumerable<JsonObject> resources, string ownerId, CancellationToken ct)
    {
        foreach (var resource in resources)
            await StageAsync(kind, resource, ownerId, ct).ConfigureAwait(false);
    }

    // The item, its counts and the move to the project's releases commit together, so a retried configuration counts once.
    private void CommitProjectConfiguration()
    {
        var cursor = _ctx.Cursor;

        CommitPending();

        _ctx.InItem = false;
        _ctx.ItemsCommitted++;
        cursor.Phase = OctopusHistoryFetchPhase.Releases;
        cursor.Skip = 0;

        TrackBytes();
    }

    // Teams a manual intervention names, feeds a package comes from and environments an action is scoped to; any not staged
    // (a system team, a resource created during the fetch) is fetched by the Closure phase.
    private void RecordProcessReferences(JsonObject process)
    {
        foreach (var action in ProcessActions(process))
        {
            foreach (var teamId in (ReadString(action["Properties"] as JsonObject, ResponsibleTeamIdsProperty) ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                RecordReference(OctopusResourceKind.Team, teamId);

            foreach (var package in (action["Packages"] as JsonArray ?? []).OfType<JsonObject>())
                RecordReference(OctopusResourceKind.Feed, ReadString(package, "FeedId"));

            foreach (var environmentId in ReadStrings(action, "Environments").Concat(ReadStrings(action, "ExcludedEnvironments")))
                RecordReference(OctopusResourceKind.Environment, environmentId);
        }
    }

    private static IEnumerable<JsonObject> ProcessActions(JsonObject process)
        => (process?["Steps"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(step => (step["Actions"] as JsonArray ?? []).OfType<JsonObject>());

    private string ProjectChildPath(string projectId, string child) => $"{SpacePrefix}projects/{Escape(projectId)}/{child}";

    private sealed class ProjectConfigurationDocuments
    {
        public string ProjectId { get; init; }
        public JsonObject Process { get; init; }
        public JsonObject Settings { get; init; }
        public JsonObject Variables { get; init; }
        public IReadOnlyList<JsonObject> Channels { get; init; }
        public IReadOnlyList<JsonObject> Runbooks { get; init; }
        public IReadOnlyList<JsonObject> Triggers { get; init; }
    }
}
