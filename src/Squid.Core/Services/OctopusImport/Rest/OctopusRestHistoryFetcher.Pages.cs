using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher
{
    public const string FeedIdProperty = "FeedId";
    public const string DeployedToMachineNamesProperty = "DeployedToMachineNames";

    // ── Releases ───────────────────────────────────────────────────────────

    private async Task FetchReleasePageAsync(CancellationToken ct)
    {
        var page = await GetPageOrEmptyAsync<OctopusReleaseDto>(ProjectReleasesPath(CurrentProjectId), ct).ConfigureAwait(false);

        foreach (var release in page.Items)
        {
            await EnrichFeedIdsAsync(release, ct).ConfigureAwait(false);
            await WriteAsync(OctopusResourceKind.Release, release, ct).ConfigureAwait(false);

            _ctx.Cursor.Counts.Releases++;
        }

        Advance(page, OctopusHistoryFetchPhase.Deployments);
    }

    // The export's release documents carry no feed per package; the importer needs it to create the release.
    private async Task EnrichFeedIdsAsync(OctopusReleaseDto release, CancellationToken ct)
    {
        foreach (var package in (release.SelectedPackages ?? []).Where(p => !(p.ExtensionData ?? []).ContainsKey(FeedIdProperty)))
        {
            var process = await ProcessSnapshotAsync(release.ProjectDeploymentProcessSnapshotId, ct).ConfigureAwait(false);
            var feedId = OctopusImportReleasePackageFeedResolver.GetSourceFeedIdFromProcess(process, package);

            if (feedId == null) continue;

            package.ExtensionData ??= [];
            package.ExtensionData[FeedIdProperty] = JsonSerializer.SerializeToElement(feedId);
        }
    }

    private async Task<OctopusDeploymentProcessDto> ProcessSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)) return null;

        if (_ctx.ProcessSnapshots.TryGetValue(snapshotId, out var cached)) return cached;

        var process = await GetOrNullAsync<OctopusDeploymentProcessDto>($"{SpacePrefix}deploymentprocesses/{Escape(snapshotId)}", ct).ConfigureAwait(false);

        _ctx.ProcessSnapshots[snapshotId] = process;

        return process;
    }

    // ── Deployments and their tasks ───────────────────────────────────────

    private async Task FetchDeploymentPageAsync(CancellationToken ct)
    {
        var page = await GetPageOrEmptyAsync<OctopusDeploymentDto>(DeploymentsPath(CurrentProjectId), ct).ConfigureAwait(false);
        var tasks = await TaskSummariesAsync(page.Items, ct).ConfigureAwait(false);

        foreach (var deployment in page.Items)
            await WriteDeploymentUnitAsync(deployment, tasks.GetValueOrDefault(deployment.TaskId ?? string.Empty), ct).ConfigureAwait(false);

        Advance(page, OctopusHistoryFetchPhase.Releases);
    }

    private async Task<Dictionary<string, OctopusServerTaskDto>> TaskSummariesAsync(IReadOnlyCollection<OctopusDeploymentDto> deployments, CancellationToken ct)
    {
        var ids = deployments.Select(d => d.TaskId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (ids.Count == 0) return new Dictionary<string, OctopusServerTaskDto>(StringComparer.OrdinalIgnoreCase);

        var page = await _ctx.Client.GetAsync<OctopusRestPage<OctopusServerTaskDto>>(TasksPath(ids), ct).ConfigureAwait(false);

        return (page?.Items ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Id)).DistinctBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
    }

    // A deployment and its task are always written together, so a re-page rewrites both or neither.
    private async Task WriteDeploymentUnitAsync(OctopusDeploymentDto deployment, OctopusServerTaskDto task, CancellationToken ct)
    {
        if (task != null)
        {
            FillTaskOwners(task, deployment);
            await AttachDetailsAsync(deployment, task, ct).ConfigureAwait(false);
            await WriteAsync(OctopusResourceKind.ServerTask, task, ct).ConfigureAwait(false);

            _ctx.Cursor.Counts.ServerTasks++;
        }

        await AttachMachineNamesAsync(deployment, ct).ConfigureAwait(false);
        await WriteAsync(OctopusResourceKind.Deployment, deployment, ct).ConfigureAwait(false);

        _ctx.Cursor.Counts.Deployments++;
    }

    private static void FillTaskOwners(OctopusServerTaskDto task, OctopusDeploymentDto deployment)
    {
        task.ProjectId ??= deployment.ProjectId;
        task.EnvironmentId ??= deployment.EnvironmentId;
    }

    private async Task AttachMachineNamesAsync(OctopusDeploymentDto deployment, CancellationToken ct)
    {
        if (deployment.DeployedToMachineIds is not { Count: > 0 }) return;

        var names = await MachineNamesAsync(ct).ConfigureAwait(false);

        deployment.ExtensionData ??= [];
        deployment.ExtensionData[DeployedToMachineNamesProperty] = JsonSerializer.SerializeToElement(deployment.DeployedToMachineIds.Select(id => names.GetValueOrDefault(id) ?? id).ToList());
    }

    private async Task<Dictionary<string, string>> MachineNamesAsync(CancellationToken ct)
    {
        if (_ctx.MachineNames != null) return _ctx.MachineNames;

        var machines = await GetOrNullAsync<List<OctopusRestMachineDto>>($"{SpacePrefix}machines/all", ct).ConfigureAwait(false) ?? [];

        _ctx.MachineNames = machines.Where(m => !string.IsNullOrWhiteSpace(m.Id)).DistinctBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(m => m.Id, m => m.Name, StringComparer.OrdinalIgnoreCase);

        return _ctx.MachineNames;
    }

    // ── Paging ─────────────────────────────────────────────────────────────

    private string CurrentProjectId => _ctx.Cursor.ProjectIds[_ctx.Cursor.ProjectIndex];

    private void Advance<T>(OctopusRestPage<T> page, OctopusHistoryFetchPhase nextPhase)
    {
        var cursor = _ctx.Cursor;
        var fetched = cursor.Skip + page.Items.Count;

        if (!_ctx.Client.IsLastPage(page.Items.Count, fetched, page.TotalResults))
        {
            cursor.Skip = fetched;
            return;
        }

        cursor.Phase = nextPhase;
        cursor.Skip = 0;

        if (nextPhase == OctopusHistoryFetchPhase.Releases) cursor.ProjectIndex++;
    }

    // A project deleted in Octopus since it was imported has no history left to fetch.
    private async Task<OctopusRestPage<T>> GetPageOrEmptyAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            return await _ctx.Client.GetPageAsync<T>(path, _ctx.Cursor.Skip, ct).ConfigureAwait(false) ?? new OctopusRestPage<T>();
        }
        catch (OctopusRestException ex) when (ex.Kind == OctopusRestErrorKind.NotFound)
        {
            return new OctopusRestPage<T>();
        }
    }

    private async Task<T> GetOrNullAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            return await _ctx.Client.GetAsync<T>(path, ct).ConfigureAwait(false);
        }
        catch (OctopusRestException ex) when (ex.Kind == OctopusRestErrorKind.NotFound)
        {
            return null;
        }
    }

    private async Task WriteAsync(OctopusResourceKind kind, OctopusDocumentDto document, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(document?.Id)) return;

        await bundleWriter.WriteDocumentAsync(_ctx.Context.SessionDirectory, kind, document, ct).ConfigureAwait(false);
    }

    // ── Paths ──────────────────────────────────────────────────────────────

    private string SpacePrefix => _ctx.Cursor.Spaceless ? "api/" : $"api/{Escape(_ctx.Cursor.OctopusSpaceId)}/";

    private string ProjectReleasesPath(string projectId) => $"{SpacePrefix}projects/{Escape(projectId)}/releases";

    private string DeploymentsPath(string projectId) => $"{SpacePrefix}deployments?projects={Escape(projectId)}";

    private string TasksPath(IReadOnlyCollection<string> ids)
        => $"api/tasks?ids={string.Join(",", ids.Select(Escape))}&skip=0&take={ids.Count}" + (_ctx.Cursor.Spaceless ? string.Empty : $"&spaces={Escape(_ctx.Cursor.OctopusSpaceId)}");

    private string TaskDetailsPath(string taskId) => $"{SpacePrefix}tasks/{Escape(taskId)}/details?verbose=false";

    private string InterruptionsPath(string taskId) => $"{SpacePrefix}interruptions?regarding={Escape(taskId)}";

    private string EventsPath(string deploymentId) => $"{SpacePrefix}events?regarding={Escape(deploymentId)}&documentTypes=Deployments,ServerTasks,Interruptions";

    private string ArtifactsPath(string taskId) => $"{SpacePrefix}artifacts?regarding={Escape(taskId)}";

    private static string Escape(string value) => Uri.EscapeDataString(value ?? string.Empty);
}
