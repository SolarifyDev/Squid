using System.Data;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Runbooks.Exceptions;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.Core.Services.Deployments.Runbooks;

public interface IRunbookPublicationDataProvider : IScopedDependency
{
    Task<RunbookPublishedSnapshotDto> PublishAsync(int id, int projectId, int spaceId, int publishedBy, CancellationToken ct);

    Task<RunbookPublishedSnapshotDto> GetPublishedAsync(int runbookId, int snapshotId, CancellationToken ct);

    Task<RunbookPublishedSnapshotData> GetPublishedDataAsync(int runbookId, int snapshotId, CancellationToken ct);
}

public sealed record RunbookPublishedSnapshotData(
    RunbookPublishedSnapshotDto Snapshot,
    DeploymentProcessSnapshotDataDto Process);

public interface IRunbookPublicationWriteObserver : IScopedDependency
{
    Task AfterSnapshotSavedBeforePointerUpdateAsync(
        int runbookId, int snapshotId, CancellationToken cancellationToken = default);
}

public sealed class NoOpRunbookPublicationWriteObserver : IRunbookPublicationWriteObserver
{
    public Task AfterSnapshotSavedBeforePointerUpdateAsync(
        int runbookId, int snapshotId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class RunbookPublicationDataProvider(
    IRepository repository,
    IUnitOfWork unitOfWork,
    IDeploymentSnapshotService snapshotService,
    IRunbookPublicationWriteObserver writeObserver) : IRunbookPublicationDataProvider
{
    public async Task<RunbookPublishedSnapshotDto> PublishAsync(
        int id, int projectId, int spaceId, int publishedBy, CancellationToken ct)
    {
        // Capture reads multiple process tables. Use one consistent database view
        // and let a conflicting write to the Runbook invalidate the publication.
        await using var transaction = await repository.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);

        try
        {
            var runbook = await repository.Query<Runbook>(
                    r => r.Id == id && r.ProjectId == projectId && r.SpaceId == spaceId)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false)
                ?? throw new DeploymentEntityNotFoundException("Runbook", id);

            var draft = await snapshotService.CaptureRunbookDraftAsync(runbook, ct).ConfigureAwait(false);
            RunbookPublicationValidator.EnsureSupported(draft);

            var version = (await repository.QueryNoTracking<RunbookSnapshot>(s => s.RunbookId == runbook.Id)
                .Select(s => (int?)s.Version)
                .MaxAsync(ct)
                .ConfigureAwait(false) ?? 0) + 1;
            var blob = UtilService.BuildSnapshotBlob(draft);
            var snapshot = new RunbookSnapshot
            {
                RunbookId = runbook.Id,
                Version = version,
                PublishedAt = DateTimeOffset.UtcNow,
                PublishedBy = publishedBy,
                SnapshotData = blob.CompressedData,
                ContentHash = blob.ContentHash,
                CompressionType = "GZIP",
                UncompressedSize = blob.UncompressedSize
            };
            await repository.InsertAsync(snapshot, ct).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            await writeObserver.AfterSnapshotSavedBeforePointerUpdateAsync(runbook.Id, snapshot.Id, ct)
                .ConfigureAwait(false);

            runbook.PublishedSnapshotId = snapshot.Id;
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return new RunbookPublishedSnapshotDto
            {
                Id = snapshot.Id,
                RunbookId = snapshot.RunbookId,
                Version = snapshot.Version,
                PublishedAt = snapshot.PublishedAt,
                PublishedBy = snapshot.PublishedBy
            };
        }
        catch (Exception ex) when (RunbookDbExceptionMapper.IsPublicationRace(ex))
        {
            throw new RunbookConflictException(
                "runbook.publish_conflict", "The Runbook changed while publishing. Refresh and try again.");
        }
    }

    public Task<RunbookPublishedSnapshotDto> GetPublishedAsync(int runbookId, int snapshotId, CancellationToken ct) =>
        repository.QueryNoTracking<RunbookSnapshot>(s => s.RunbookId == runbookId && s.Id == snapshotId)
            .Select(s => new RunbookPublishedSnapshotDto
            {
                Id = s.Id,
                RunbookId = s.RunbookId,
                Version = s.Version,
                PublishedAt = s.PublishedAt,
                PublishedBy = s.PublishedBy
            })
            .SingleOrDefaultAsync(ct);

    public async Task<RunbookPublishedSnapshotData> GetPublishedDataAsync(
        int runbookId,
        int snapshotId,
        CancellationToken ct)
    {
        var snapshot = await repository.QueryNoTracking<RunbookSnapshot>(
                s => s.RunbookId == runbookId && s.Id == snapshotId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (snapshot == null)
            return null;

        return new RunbookPublishedSnapshotData(
            new RunbookPublishedSnapshotDto
            {
                Id = snapshot.Id,
                RunbookId = snapshot.RunbookId,
                Version = snapshot.Version,
                PublishedAt = snapshot.PublishedAt,
                PublishedBy = snapshot.PublishedBy
            },
            UtilService.DecompressFromGzip<DeploymentProcessSnapshotDataDto>(snapshot.SnapshotData));
    }
}
