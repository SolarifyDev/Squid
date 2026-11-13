using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher
{
    public const string FeedIdProperty = "FeedId";
    public const string DeployedToMachineNamesProperty = "DeployedToMachineNames";

    // ── Releases ───────────────────────────────────────────────────────────

    private async Task FetchReleasePageAsync(CancellationToken ct)
    {
        var page = await ReleasePageAsync(ct).ConfigureAwait(false);

        foreach (var release in page.Items)
        {
            if (ChunkBudgetSpent()) return;

            _ctx.InItem = true;
            await EnrichFeedIdsAsync(release, ct).ConfigureAwait(false);
            await WriteAsync(OctopusResourceKind.Release, release, ct).ConfigureAwait(false);

            _ctx.Pending.Releases++;
            CommitItem();
        }

        Advance(page, OctopusHistoryFetchPhase.Deployments);
    }

    // A project the space does not have (deleted in Octopus, or another space chosen) has no history; it is reported.
    private async Task<OctopusRestPage<OctopusReleaseDto>> ReleasePageAsync(CancellationToken ct)
    {
        var page = await GetPageOrNullAsync<OctopusReleaseDto>(ProjectReleasesPath(CurrentProjectId), ct).ConfigureAwait(false);

        if (page != null) return page;

        _ctx.Cursor.Counts.ProjectsNotFound++;

        return new OctopusRestPage<OctopusReleaseDto>();
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
        var page = await GetPageOrNullAsync<OctopusDeploymentDto>(DeploymentsPath(CurrentProjectId), ct).ConfigureAwait(false) ?? new OctopusRestPage<OctopusDeploymentDto>();
        var tasks = await TaskSummariesAsync(page.Items, ct).ConfigureAwait(false);

        foreach (var deployment in page.Items)
        {
            if (ChunkBudgetSpent()) return;

            _ctx.InItem = true;
            await WriteDeploymentUnitAsync(deployment, tasks.GetValueOrDefault(deployment.TaskId ?? string.Empty), ct).ConfigureAwait(false);
            CommitItem();
        }

        Advance(page, OctopusHistoryFetchPhase.Releases);
    }

    private async Task<Dictionary<string, OctopusServerTaskDto>> TaskSummariesAsync(IReadOnlyCollection<OctopusDeploymentDto> deployments, CancellationToken ct)
    {
        var ids = deployments.Select(d => d.TaskId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (ids.Count == 0) return new Dictionary<string, OctopusServerTaskDto>(StringComparer.OrdinalIgnoreCase);

        var page = await GetOrNullAsync<OctopusRestPage<OctopusServerTaskDto>>(TasksPath(ids), ct, tolerateNotFound: false).ConfigureAwait(false);

        return (page?.Items ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Id)).DistinctBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
    }

    // A deployment and its task are always written together, so a re-page rewrites both or neither. Every read comes
    // first: an item abandoned while reading leaves none of its documents in the bundle.
    private async Task WriteDeploymentUnitAsync(OctopusDeploymentDto deployment, OctopusServerTaskDto task, CancellationToken ct)
    {
        var details = await ReadTaskDetailsAsync(deployment, task, ct).ConfigureAwait(false);

        await AttachMachineNamesAsync(deployment, ct).ConfigureAwait(false);

        await WriteTaskAsync(task, details, ct).ConfigureAwait(false);
        await WriteAsync(OctopusResourceKind.Deployment, deployment, ct).ConfigureAwait(false);

        _ctx.Pending.Deployments++;
    }

    private async Task<OctopusTaskHistoryDetails> ReadTaskDetailsAsync(OctopusDeploymentDto deployment, OctopusServerTaskDto task, CancellationToken ct)
    {
        if (task == null) return null;

        FillTaskOwners(task, deployment);

        return await ReadDetailsAsync(deployment, task, ct).ConfigureAwait(false);
    }

    private async Task WriteTaskAsync(OctopusServerTaskDto task, OctopusTaskHistoryDetails details, CancellationToken ct)
    {
        if (task == null) return;

        await WriteDetailsAsync(task, details, ct).ConfigureAwait(false);
        await WriteAsync(OctopusResourceKind.ServerTask, task, ct).ConfigureAwait(false);

        _ctx.Pending.ServerTasks++;
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

    // The time budget is checked per item, so one slow page cannot outlast the request; the first item always runs.
    private bool ChunkBudgetSpent() => _ctx.ItemsCommitted > 0 && _ctx.Clock.Elapsed >= _options.ChunkDuration;

    // An item's counts and its place in the cursor move together, so an item retried after a failure is counted once.
    private void CommitItem()
    {
        CommitPending();

        _ctx.InItem = false;
        _ctx.Cursor.Skip++;
        _ctx.ItemsCommitted++;

        TrackBytes();
    }

    private void CommitPending()
    {
        AddCounts(_ctx.Cursor.Counts, _ctx.Pending);
        AddSkipped(_ctx.Cursor, _ctx.PendingSkipped);

        _ctx.Pending = new OctopusHistoryFetchCountsDto();
        _ctx.PendingSkipped = [];
    }

    private static void AddCounts(OctopusHistoryFetchCountsDto total, OctopusHistoryFetchCountsDto item)
    {
        total.Releases += item.Releases;
        total.Deployments += item.Deployments;
        total.ServerTasks += item.ServerTasks;
        total.DetailsFetched += item.DetailsFetched;
        total.DetailsSkipped += item.DetailsSkipped;
        total.DetailsTooLarge += item.DetailsTooLarge;
        total.ProjectsNotFound += item.ProjectsNotFound;
        total.ItemsRejected += item.ItemsRejected;
        total.ItemsAbandoned += item.ItemsAbandoned;
    }

    // Skip already counts every item of the page written, so it is where the page ended. A refused page commits its record here.
    private void Advance<T>(OctopusRestPage<T> page, OctopusHistoryFetchPhase nextPhase)
    {
        var cursor = _ctx.Cursor;

        CommitPending();

        if (!OctopusRestClient.IsLastPage(page.Items.Count, cursor.Skip, page.TotalResults)) return;

        cursor.Phase = nextPhase;
        cursor.Skip = 0;

        if (nextPhase == OctopusHistoryFetchPhase.Releases) cursor.ProjectIndex++;
    }

    // Not found is null: a deleted project, or one the chosen space does not have. A refused page is an empty page, recorded.
    private async Task<OctopusRestPage<T>> GetPageOrNullAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            return await _ctx.Client.GetPageAsync<T>(path, _ctx.Cursor.Skip, ct).ConfigureAwait(false) ?? new OctopusRestPage<T>();
        }
        catch (OctopusRestException ex) when (ex.Kind == OctopusRestErrorKind.NotFound)
        {
            return null;
        }
        catch (OctopusRestException ex) when (ex.Kind == OctopusRestErrorKind.Rejected)
        {
            RecordRejected(_ctx.Client.LastRequestedPath);
            return new OctopusRestPage<T>();
        }
    }

    private async Task<T> GetOrNullAsync<T>(string path, CancellationToken ct, bool tolerateNotFound = true) where T : class
    {
        try
        {
            return await _ctx.Client.GetAsync<T>(path, ct).ConfigureAwait(false);
        }
        catch (OctopusRestException ex) when (ex.Kind == OctopusRestErrorKind.NotFound && tolerateNotFound)
        {
            return null;
        }
        catch (OctopusRestException ex) when (ex.Kind == OctopusRestErrorKind.Rejected)
        {
            RecordRejected(path);
            return null;
        }
    }

    private async Task<List<T>> GetAllPagesOrEmptyAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            return await _ctx.Client.GetAllPagesAsync<T>(path, ct).ConfigureAwait(false);
        }
        catch (OctopusRestException ex) when (ex.Kind == OctopusRestErrorKind.Rejected)
        {
            RecordRejected(path);
            return [];
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

    // The importer keeps at most MaxLogLinesPerTask lines, so Octopus is asked for no more than that per activity.
    private string TaskDetailsPath(string taskId) => $"{SpacePrefix}tasks/{Escape(taskId)}/details?verbose=false&tail={OctopusImportDeploymentHistoryImporter.MaxLogLinesPerTask()}";

    private string InterruptionsPath(string taskId) => $"{SpacePrefix}interruptions?regarding={Escape(taskId)}";

    private string EventsPath(string deploymentId) => $"{SpacePrefix}events?regarding={Escape(deploymentId)}&documentTypes=Deployments,ServerTasks,Interruptions";

    private string ArtifactsPath(string taskId) => $"{SpacePrefix}artifacts?regarding={Escape(taskId)}";

    private static string Escape(string value) => Uri.EscapeDataString(value ?? string.Empty);
}
