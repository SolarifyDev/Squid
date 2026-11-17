using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher
{
    // Details are fetched unless the source map already holds them for an unchanged task (incremental re-fetch).
    // Only reads happen here; the details are written with the task, after every read of the deployment unit.
    private async Task<OctopusTaskHistoryDetails> ReadDetailsAsync(OctopusDeploymentDto deployment, OctopusServerTaskDto task, CancellationToken ct)
    {
        if (IsUnchangedWithStoredDetails(deployment, task))
        {
            ReadCounts.DetailsSkipped++;
            return null;
        }

        var rejectedBefore = ReadCounts.ItemsRejected;
        var details = await FetchDetailsAsync(deployment, task, ct).ConfigureAwait(false);

        if (ReadCounts.ItemsRejected == rejectedBefore) return details;

        task.DetailsIncomplete = true;

        return null;
    }

    // A partial set (one list refused) would be taken as the task's complete details, so it is never written.
    private async Task WriteDetailsAsync(OctopusServerTaskDto task, OctopusTaskHistoryDetails details, CancellationToken ct)
    {
        if (details == null) return;

        var write = await detailsStore.WriteAsync(_ctx.Context.SessionId, task.Id, details, ct).ConfigureAwait(false);

        _ctx.Cursor.DetailsBytes += write.Growth;

        if (write.Created) _ctx.PendingStagedDetails++;

        task.DetailLevel = 1;
        task.DetailFingerprint = OctopusImportFingerprint.ForTaskDetails(details, OctopusImportDeploymentHistoryImporter.MaxLogLinesPerTask());

        _ctx.Pending.DetailsFetched++;
    }

    /// <summary>The only source-map kinds the fetch reads (<see cref="IsUnchangedWithStoredDetails(OctopusImportPriorMappings, OctopusDeploymentDto, OctopusServerTaskDto)"/>); every chunk loads these, never the whole space.</summary>
    public static IReadOnlyList<string> PriorMappingKinds { get; } = [nameof(OctopusResourceKind.Deployment), nameof(OctopusResourceKind.ServerTask)];

    /// <summary>The incremental skip: the source map holds details for this deployment, its imported task row still exists and the task has not changed since.</summary>
    public static bool IsUnchangedWithStoredDetails(OctopusImportPriorMappings prior, OctopusDeploymentDto deployment, OctopusServerTaskDto task)
        => prior.TryGet(OctopusResourceKind.Deployment.ToString(), deployment.Id, out var stored) && stored.DetailFingerprint != null && stored.Fingerprint == OctopusImportFingerprint.ForTask(task) && prior.TryGet(OctopusResourceKind.ServerTask.ToString(), task.Id, out _);

    private bool IsUnchangedWithStoredDetails(OctopusDeploymentDto deployment, OctopusServerTaskDto task)
        => IsUnchangedWithStoredDetails(_ctx.Context.PriorMappings ?? OctopusImportPriorMappings.Empty, deployment, task);

    // The three lists are read side by side once the task's details are in; a task without details asks for none of them.
    private async Task<OctopusTaskHistoryDetails> FetchDetailsAsync(OctopusDeploymentDto deployment, OctopusServerTaskDto task, CancellationToken ct)
    {
        var taskDetails = await TaskDetailsOrNullAsync(task.Id, ct).ConfigureAwait(false);

        if (taskDetails == null) return null;

        using var lists = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var interruptions = ReadListAsync<OctopusInterruptionDto>(InterruptionsPath(task.Id), lists);
        var events = ReadListAsync<OctopusEventDto>(EventsPath(deployment.Id), lists);
        var artifacts = ReadListAsync<OctopusArtifactDto>(ArtifactsPath(task.Id), lists);

        await Task.WhenAll(interruptions, events, artifacts).ConfigureAwait(false);

        return new OctopusTaskHistoryDetails
        {
            ActivityLogs = taskDetails.ActivityLogs ?? [],
            ActivityLog = taskDetails.ActivityLog,
            Interruptions = AddToCurrentScope(await interruptions.ConfigureAwait(false)),
            Events = AddToCurrentScope(await events.ConfigureAwait(false)),
            Artifacts = AddToCurrentScope(await artifacts.ConfigureAwait(false))
        };
    }

    // A list that fails cancels its siblings, so the item fails as soon as one list does, not after the others have paged on.
    // The siblings it cuts off end cancelled, not failed, so Task.WhenAll rethrows the first failed list in the order above.
    private async Task<Scoped<List<T>>> ReadListAsync<T>(string path, CancellationTokenSource siblings)
    {
        try
        {
            return await InNewScopeAsync(() => GetAllPagesOrEmptyAsync<T>(path, siblings.Token)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await siblings.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    // A task whose log is over the per-response cap even after the tail is imported without details, not fatal.
    // Only the single-response cap throws TooLarge here; the history byte budget is enforced between items and in lists.
    private async Task<OctopusRestTaskDetailsDto> TaskDetailsOrNullAsync(string taskId, CancellationToken ct)
    {
        try
        {
            return await GetOrNullAsync<OctopusRestTaskDetailsDto>(TaskDetailsPath(taskId), ct).ConfigureAwait(false);
        }
        catch (OctopusRestException ex) when (ex.Kind == OctopusRestErrorKind.TooLarge)
        {
            ReadCounts.DetailsTooLarge++;
            return null;
        }
    }
}
