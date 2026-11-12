using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher
{
    // Details are fetched unless the source map already holds them for an unchanged task (incremental re-fetch).
    private async Task AttachDetailsAsync(OctopusDeploymentDto deployment, OctopusServerTaskDto task, CancellationToken ct)
    {
        if (IsUnchangedWithStoredDetails(deployment, task))
        {
            _ctx.Cursor.Counts.DetailsSkipped++;
            return;
        }

        var details = await FetchDetailsAsync(deployment, task, ct).ConfigureAwait(false);

        if (details == null) return;

        await detailsStore.WriteAsync(_ctx.Context.SessionDirectory, task.Id, details, ct).ConfigureAwait(false);

        task.DetailLevel = 1;
        task.DetailFingerprint = OctopusImportFingerprint.ForTaskDetails(details, OctopusImportDeploymentHistoryImporter.MaxLogLinesPerTask());

        _ctx.Cursor.Counts.DetailsFetched++;
    }

    /// <summary>The incremental skip: the source map holds details for this deployment, its imported task row still exists and the task has not changed since.</summary>
    public static bool IsUnchangedWithStoredDetails(OctopusImportPriorMappings prior, OctopusDeploymentDto deployment, OctopusServerTaskDto task)
        => prior.TryGet(OctopusResourceKind.Deployment.ToString(), deployment.Id, out var stored) && stored.DetailFingerprint != null && stored.Fingerprint == OctopusImportFingerprint.ForTask(task) && prior.TryGet(OctopusResourceKind.ServerTask.ToString(), task.Id, out _);

    private bool IsUnchangedWithStoredDetails(OctopusDeploymentDto deployment, OctopusServerTaskDto task)
        => IsUnchangedWithStoredDetails(_ctx.Context.PriorMappings ?? OctopusImportPriorMappings.Empty, deployment, task);

    private async Task<OctopusTaskHistoryDetails> FetchDetailsAsync(OctopusDeploymentDto deployment, OctopusServerTaskDto task, CancellationToken ct)
    {
        var taskDetails = await GetOrNullAsync<OctopusRestTaskDetailsDto>(TaskDetailsPath(task.Id), ct).ConfigureAwait(false);

        if (taskDetails == null) return null;

        return new OctopusTaskHistoryDetails
        {
            ActivityLogs = taskDetails.ActivityLogs ?? [],
            ActivityLog = taskDetails.ActivityLog,
            Interruptions = await _ctx.Client.GetAllPagesAsync<OctopusInterruptionDto>(InterruptionsPath(task.Id), ct).ConfigureAwait(false),
            Events = await _ctx.Client.GetAllPagesAsync<OctopusEventDto>(EventsPath(deployment.Id), ct).ConfigureAwait(false),
            Artifacts = await _ctx.Client.GetAllPagesAsync<OctopusArtifactDto>(ArtifactsPath(task.Id), ct).ConfigureAwait(false)
        };
    }
}
