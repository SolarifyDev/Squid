using System.Text.Json;
using System.Text.Json.Nodes;
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

        if (StepBackIfShrunk(page)) return;

        if (!await ReadAheadThenWriteInOrderAsync(page.Items, new ItemSteps<OctopusReleaseDto, ReleaseRead>(ReadReleaseAsync, WriteReleaseAsync), ct).ConfigureAwait(false)) return;

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

    // The process snapshot is read only for a release with a package whose feed the export does not carry.
    private async Task<ReleaseRead> ReadReleaseAsync(OctopusReleaseDto release, CancellationToken ct)
    {
        var process = PackagesWithoutFeed(release).Any() ? await ProcessSnapshotAsync(release.ProjectDeploymentProcessSnapshotId, ct).ConfigureAwait(false) : null;

        return new ReleaseRead(release, process);
    }

    private async Task WriteReleaseAsync(ReleaseRead read, CancellationToken ct)
    {
        EnrichFeedIds(read.Release, read.Process);

        await WriteAsync(OctopusResourceKind.Release, read.Release, ct).ConfigureAwait(false);
        RecordReference(OctopusResourceKind.Channel, read.Release.ChannelId);

        _ctx.Pending.Releases++;
    }

    // The export's release documents carry no feed per package; the importer needs it to create the release.
    private void EnrichFeedIds(OctopusReleaseDto release, OctopusDeploymentProcessDto process)
    {
        foreach (var package in PackagesWithoutFeed(release))
        {
            var feedId = OctopusImportReleasePackageFeedResolver.GetSourceFeedIdFromProcess(process, package);

            if (feedId == null) continue;

            RecordReference(OctopusResourceKind.Feed, feedId);

            package.ExtensionData ??= [];
            package.ExtensionData[FeedIdProperty] = JsonSerializer.SerializeToElement(feedId);
        }
    }

    private static IEnumerable<OctopusSelectedPackageDto> PackagesWithoutFeed(OctopusReleaseDto release) => (release.SelectedPackages ?? []).Where(p => !(p.ExtensionData ?? []).ContainsKey(FeedIdProperty));

    private Task<OctopusDeploymentProcessDto> ProcessSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)) return Task.FromResult<OctopusDeploymentProcessDto>(null);

        return ReadOnceAsync(_ctx.ProcessSnapshots, snapshotId, () => GetOrNullAsync<OctopusDeploymentProcessDto>($"{SpacePrefix}deploymentprocesses/{Escape(snapshotId)}", ct));
    }

    // ── Deployments and their tasks ───────────────────────────────────────

    private async Task FetchDeploymentPageAsync(CancellationToken ct)
    {
        var page = await GetPageOrNullAsync<OctopusDeploymentDto>(DeploymentsPath(CurrentProjectId), ct).ConfigureAwait(false) ?? new OctopusRestPage<OctopusDeploymentDto>();

        if (StepBackIfShrunk(page)) return;

        var tasks = await TaskSummariesAsync(page.Items, ct).ConfigureAwait(false);

        Task<DeploymentRead> Read(OctopusDeploymentDto deployment, CancellationToken readCt) => ReadDeploymentAsync(deployment, tasks.GetValueOrDefault(deployment.TaskId ?? string.Empty), readCt);

        if (!await ReadAheadThenWriteInOrderAsync(page.Items, new ItemSteps<OctopusDeploymentDto, DeploymentRead>(Read, WriteDeploymentAsync), ct).ConfigureAwait(false)) return;

        Advance(page, OctopusHistoryFetchPhase.ProjectConfiguration);
    }

    private async Task<Dictionary<string, OctopusServerTaskDto>> TaskSummariesAsync(IReadOnlyCollection<OctopusDeploymentDto> deployments, CancellationToken ct)
    {
        var ids = deployments.Select(d => d.TaskId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (ids.Count == 0) return new Dictionary<string, OctopusServerTaskDto>(StringComparer.OrdinalIgnoreCase);

        var page = await GetOrNullAsync<OctopusRestPage<OctopusServerTaskDto>>(TasksPath(ids), ct, tolerateNotFound: false).ConfigureAwait(false);

        return (page?.Items ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Id)).DistinctBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
    }

    // Every read of a deployment unit (its task's details, its machine names) happens before any of its writes.
    private async Task<DeploymentRead> ReadDeploymentAsync(OctopusDeploymentDto deployment, OctopusServerTaskDto task, CancellationToken ct)
    {
        var details = ReadTaskDetailsAsync(deployment, task, ct);
        var machineNames = AttachMachineNamesAsync(deployment, ct);

        await Task.WhenAll(details, machineNames).ConfigureAwait(false);

        return new DeploymentRead(deployment, task, await details.ConfigureAwait(false));
    }

    // A deployment and its task are always written together, so a re-page rewrites both or neither.
    private async Task WriteDeploymentAsync(DeploymentRead read, CancellationToken ct)
    {
        await WriteTaskAsync(read.Task, read.Details, ct).ConfigureAwait(false);
        await WriteAsync(OctopusResourceKind.Deployment, read.Deployment, ct).ConfigureAwait(false);

        RecordReference(OctopusResourceKind.Environment, read.Deployment.EnvironmentId);
        RecordReference(OctopusResourceKind.Channel, read.Deployment.ChannelId);

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

    // Names come from the machine documents the catalog staged; a machine Octopus no longer has keeps its id as its name.
    private async Task AttachMachineNamesAsync(OctopusDeploymentDto deployment, CancellationToken ct)
    {
        if (deployment.DeployedToMachineIds is not { Count: > 0 }) return;

        var names = new List<string>();

        foreach (var machineId in deployment.DeployedToMachineIds)
            names.Add(await StagedMachineNameAsync(machineId, ct).ConfigureAwait(false) ?? machineId);

        deployment.ExtensionData ??= [];
        deployment.ExtensionData[DeployedToMachineNamesProperty] = JsonSerializer.SerializeToElement(names);
    }

    private Task<string> StagedMachineNameAsync(string machineId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machineId)) return Task.FromResult<string>(null);

        return ReadOnceAsync(_ctx.MachineNames, machineId, () => ReadStagedMachineNameAsync(machineId, ct));
    }

    private async Task<string> ReadStagedMachineNameAsync(string machineId, CancellationToken ct)
    {
        var machine = await bundleWriter.ReadStagedAsync(_ctx.Context.SessionDirectory, OctopusResourceKind.Machine, machineId, ct).ConfigureAwait(false);

        return machine?["Name"] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
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
        _ctx.CurrentItem = null;
        _ctx.Cursor.Skip++;
        _ctx.ItemsCommitted++;

        TrackBytes();
    }

    // A kind restarted from its first page reads its items again; what the abandoned pass recorded is read again too.
    private void DiscardPending()
    {
        _ctx.Pending = new OctopusHistoryFetchCountsDto();
        _ctx.PendingSkipped = [];
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
        total.ConfigurationDocuments += item.ConfigurationDocuments;
        total.SecretsWithheld += item.SecretsWithheld;
        total.ConfigAsCodeProjects += item.ConfigAsCodeProjects;
    }

    // Skip already counts every item of the page written, so it is where the page ended. A refused page commits its record here.
    // The last deployment page of a project moves on to the next project's configuration.
    private void Advance<T>(OctopusRestPage<T> page, OctopusHistoryFetchPhase nextPhase)
    {
        var cursor = _ctx.Cursor;

        CommitPending();

        if (!OctopusRestClient.IsLastPage(page.Items.Count, cursor.Skip, page.TotalResults)) return;

        cursor.Phase = nextPhase;
        cursor.Skip = 0;
        cursor.PhaseTotalResults = null;

        if (nextPhase != OctopusHistoryFetchPhase.ProjectConfiguration) return;

        cursor.ProjectIndex++;

        if (!HasCurrentProject) cursor.Phase = OctopusHistoryFetchPhase.Closure;
    }

    // Records what Octopus reports the list holds in all (shown as progress). Octopus pages by offset: items removed from the part
    // already read shift the rest towards the front, so a smaller total than the previous page reported steps the offset back by
    // the shrink and the page is read again from there. Staged documents are overwritten by kind and id, so an item read twice is
    // harmless, while continuing would pass over an unread one. A list now empty (the project deleted) has nothing left to miss.
    private bool StepBackIfShrunk<T>(OctopusRestPage<T> page)
    {
        var cursor = _ctx.Cursor;
        var shrink = cursor.PhaseTotalResults is { } previous && page.TotalResults > 0 ? previous - page.TotalResults : 0;

        cursor.PhaseTotalResults = page.TotalResults;

        if (shrink <= 0 || cursor.Skip == 0) return false;

        cursor.Skip = Math.Max(0, cursor.Skip - shrink);

        return true;
    }

    // Not found is null: a deleted project, or one the chosen space does not have. A refused first page is an empty list, recorded.
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
        catch (OctopusRestException ex) when (ex.Kind == OctopusRestErrorKind.Rejected && _ctx.Cursor.Skip == 0)
        {
            RecordRejected(_ctx.Client.LastRequestedPath);
            return new OctopusRestPage<T>();
        }
        catch (OctopusRestException ex) when (ex.Kind == OctopusRestErrorKind.Rejected)
        {
            await EnsureKeyStillSufficientAsync(ct).ConfigureAwait(false);
            throw new OctopusRestException(OctopusRestErrorKind.Unreachable);
        }
    }

    // A refused page of a list whose earlier pages were read is not the list's end: treating it as one would drop the rest of the
    // list. The key's permissions are checked again (a narrowed key fails as restricted); otherwise the chunk fails with the cursor
    // still on this page, so the next chunk reads it again.
    private async Task EnsureKeyStillSufficientAsync(CancellationToken ct)
    {
        var permissions = await ReadPermissionsAsync(_ctx.Client, _ctx.OctopusUserId, _ctx.Cursor.OctopusSpaceId, ct).ConfigureAwait(false);

        OctopusRestPermissionGate.EnsureSufficient(permissions);
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

    // Not found is an empty list only when the caller says so: a task's detail list Octopus does not have is not "none".
    private async Task<List<T>> GetAllPagesOrEmptyAsync<T>(string path, CancellationToken ct, bool tolerateNotFound = false)
    {
        try
        {
            return await _ctx.Client.GetAllPagesAsync<T>(path, ct).ConfigureAwait(false);
        }
        catch (OctopusRestException ex) when (ex.Kind == OctopusRestErrorKind.NotFound && tolerateNotFound)
        {
            return [];
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
