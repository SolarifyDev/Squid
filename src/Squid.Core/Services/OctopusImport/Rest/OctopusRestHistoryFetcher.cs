using System.Collections.Concurrent;
using System.Diagnostics;
using Npgsql;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SessionFiles;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher(IOctopusRestClientFactory clientFactory, IOctopusHistoryBundleWriter bundleWriter, IOctopusImportHistoryDetailsStore detailsStore, IOctopusImportSessionFileStore files, OctopusRestOptions options = null, OctopusImportSizeLimits limits = null) : IOctopusRestHistoryFetcher
{
    /// <summary>How often the chunk deadline may abandon the same item before it is skipped.</summary>
    public const int MaxAbandonments = 3;

    /// <summary>The most skipped items the cursor lists; further ones are only counted.</summary>
    public const int MaxRecordedSkippedItems = 500;

    /// <summary>How long before the chunk's lease runs out bundling stops, so no next chunk can bundle into the same directory at the same time.</summary>
    public static readonly TimeSpan BundleLeaseSafety = TimeSpan.FromSeconds(30);

    private readonly OctopusRestOptions _options = options ?? OctopusRestOptions.FromEnvironment();
    private readonly OctopusImportSizeLimits _limits = limits ?? OctopusImportSizeLimits.FromEnvironment();

    private FetchRun _ctx;

    public async Task<OctopusHistoryFetchProgressDto> FetchChunkAsync(OctopusHistoryFetchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await FetchStagedChunkAsync(context, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw StorageFailure(ex, context.SessionDirectory);
        }
        catch (NpgsqlException ex)
        {
            throw DatabaseFailure(ex, context.SessionId);
        }
        catch (OctopusImportSessionFileUnreadableException)
        {
            throw new OctopusImportHistoryStagingLostException();
        }
    }

    // The client turns every network I/O failure into an OctopusRestException, so an IOException here is this server's own disk (full,
    // most often). Its message names absolute server paths: it is logged for the operator and replaced by a fixed one for the caller.
    private static OctopusImportStorageException StorageFailure(IOException ex, string sessionDirectory)
    {
        Log.Error(ex, "Octopus import history fetch could not write its temporary files in {SessionDirectory}", sessionDirectory);

        return new OctopusImportStorageException();
    }

    // The database's message may name the server or the statement: it is logged for the operator and replaced by a fixed one.
    private static OctopusImportStorageException DatabaseFailure(NpgsqlException ex, Guid sessionId)
    {
        Log.Error(ex, "Octopus import history fetch of session {SessionId} could not store or read its staged documents in the database", sessionId);

        return OctopusImportStorageException.Database();
    }

    private async Task<OctopusHistoryFetchProgressDto> FetchStagedChunkAsync(OctopusHistoryFetchContext context, CancellationToken ct)
    {
        await OctopusHistoryStagingMarker.EnsureIntactAsync(files, context.SessionId, context.Cursor, ct).ConfigureAwait(false);

        using var client = clientFactory.Create(context.ServerUrl, context.ApiKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        deadline.CancelAfter(_options.ChunkDeadline);
        _ctx = new FetchRun(context, client, Stopwatch.StartNew(), context.Cursor.TotalBytes);
        client.ByteBudget = _limits.MaxBytes;

        await RunWithinDeadlineAsync(deadline, ct).ConfigureAwait(false);

        return Progress(_ctx.Cursor);
    }

    public static OctopusHistoryFetchProgressDto Progress(OctopusImportHistoryFetchDto cursor)
        => new() { Phase = cursor.Phase, ProjectIndex = cursor.ProjectIndex, ProjectCount = cursor.ProjectIds.Count, CatalogIndex = cursor.CatalogIndex, CatalogKindCount = CatalogCollections.Count, PhaseItemsDone = PhaseItemsDone(cursor), PhaseItemsTotal = PhaseItemsTotal(cursor), Counts = cursor.Counts, TotalBytes = cursor.TotalBytes, Done = cursor.Done };

    // Skip counts the items committed in the paged phases; the other phases have no item list to measure.
    private static int PhaseItemsDone(OctopusImportHistoryFetchDto cursor) => PhaseItemsTotal(cursor) == null ? 0 : cursor.Skip;

    private static int? PhaseItemsTotal(OctopusImportHistoryFetchDto cursor)
        => cursor.Phase switch
        {
            OctopusHistoryFetchPhase.Catalog => cursor.CatalogTotalResults,
            OctopusHistoryFetchPhase.Releases or OctopusHistoryFetchPhase.Deployments => cursor.PhaseTotalResults,
            _ => null
        };

    // A chunk still running past its deadline is abandoned before its lease lapses, so no second chunk can overlap it.
    // The item it was on is skipped once MaxAbandonments chunks failed on it (abandoned, or the server kept failing one of
    // its requests), so one stuck or broken item cannot stall the fetch.
    private async Task RunWithinDeadlineAsync(CancellationTokenSource deadline, CancellationToken ct)
    {
        try
        {
            await RunChunkAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested && OverrunExplainedByServerPauses)
        {
            throw new OctopusRestException(OctopusRestErrorKind.Unreachable);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            if (!SkipIfFailedTooOften(OctopusHistoryFetchSkipReason.Abandoned, _ctx.InItem ? _ctx.Client.OldestInFlightPathOf(_ctx.CurrentItem) : null))
                throw new OctopusRestException(OctopusRestErrorKind.Unreachable);
        }
        catch (OctopusRestException ex) when (ex.ServerErrorPath != null && !ct.IsCancellationRequested)
        {
            if (!SkipIfFailedTooOften(OctopusHistoryFetchSkipReason.ServerError, ex.ServerErrorPath)) throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && OverranChunkBudget && !OverrunExplainedByServerPauses)
        {
            SkipIfFailedTooOften(OctopusHistoryFetchSkipReason.Abandoned, _ctx.InItem ? _ctx.Client.OldestInFlightPathOf(_ctx.CurrentItem) : null);
            throw;
        }
    }

    // A proxy shorter than the chunk deadline (nginx-ingress: 60s) cuts the request first. An item still running past the chunk's
    // budget when that happens counts as abandoned too, or it would be retried forever; a cut within the budget (a pause) does not.
    private bool OverranChunkBudget => _ctx.Clock.Elapsed >= _options.ChunkDuration;

    // Sustained throttling (429 + Retry-After) holds items in the server's own pauses. When those pauses cover the whole time
    // the chunk ran past its budget, the overrun is the server's doing: the chunk fails retryably and no item is counted.
    private bool OverrunExplainedByServerPauses => _ctx.Client.ServerRequestedPause >= _ctx.Clock.Elapsed - _options.ChunkDuration;

    private async Task RunChunkAsync(CancellationToken ct)
    {
        await PinInstallationAsync(ct).ConfigureAwait(false);
        await IdentifyKeyAsync(ct).ConfigureAwait(false);
        await ResolveSpaceAsync(ct).ConfigureAwait(false);
        await BindKeyAsync(ct).ConfigureAwait(false);
        await FetchPagesAsync(ct).ConfigureAwait(false);
    }

    // Every chunk re-reads /api: a different installation behind the same URL must not be mixed into one bundle.
    private async Task PinInstallationAsync(CancellationToken ct)
    {
        var root = await _ctx.Client.GetRootAsync(ct).ConfigureAwait(false);
        var cursor = _ctx.Cursor;

        if (!string.IsNullOrWhiteSpace(cursor.InstallationId) && OctopusImportPriorMappings.Normalize(cursor.InstallationId) != OctopusImportPriorMappings.Normalize(root.InstallationId))
            throw new OctopusImportHistoryServerMismatchException("installation");

        cursor.InstallationId = root.InstallationId;
        cursor.OctopusVersion = root.Version;
        cursor.Spaceless = root.Spaceless;
        cursor.StartedAt ??= DateTimeOffset.UtcNow;
    }

    private async Task ResolveSpaceAsync(CancellationToken ct)
    {
        var cursor = _ctx.Cursor;

        if (cursor.Spaceless || !string.IsNullOrWhiteSpace(cursor.OctopusSpaceId)) return;

        cursor.OctopusSpaceId = await ResolveSpaceIdAsync(_ctx.Client, cursor.Spaceless, null, ct).ConfigureAwait(false);
    }

    private async Task FetchPagesAsync(CancellationToken ct)
    {
        do
        {
            await FetchNextPageAsync(ct).ConfigureAwait(false);
            TrackBytes();
        }
        while (!_ctx.Cursor.Done && _ctx.Clock.Elapsed < _options.ChunkDuration);
    }

    // The phase decides first; a project index past the end only means the per-project phases are over.
    private Task FetchNextPageAsync(CancellationToken ct)
        => _ctx.Cursor.Phase switch
        {
            OctopusHistoryFetchPhase.Catalog => FetchCatalogPageAsync(ct),
            OctopusHistoryFetchPhase.ProjectConfiguration when HasCurrentProject => FetchProjectConfigurationAsync(ct),
            OctopusHistoryFetchPhase.Releases when HasCurrentProject => FetchReleasePageAsync(ct),
            OctopusHistoryFetchPhase.Deployments when HasCurrentProject => FetchDeploymentPageAsync(ct),
            OctopusHistoryFetchPhase.Closure => FetchClosureAsync(ct),
            OctopusHistoryFetchPhase.Done => CompleteAsync(ct),
            _ => EnterClosureAsync()
        };

    private bool HasCurrentProject => _ctx.Cursor.ProjectIndex < _ctx.Cursor.ProjectIds.Count;

    // Bundling is no item: it runs to the chunk's lease, not to the item deadline or the request (a proxy may drop a long request), so a
    // large bundle completes instead of being cut off and rebuilt from scratch on every resume. It never outlives the lease.
    private async Task CompleteAsync(CancellationToken ct)
    {
        var cursor = _ctx.Cursor;

        await EnsureStagingCompleteAsync(ct).ConfigureAwait(false);
        await BuildBundleWithinLeaseAsync(cursor).ConfigureAwait(false);

        cursor.Phase = OctopusHistoryFetchPhase.Done;
        cursor.Done = true;
        cursor.CompletedAt = DateTimeOffset.UtcNow;
    }

    private async Task BuildBundleWithinLeaseAsync(OctopusImportHistoryFetchDto cursor)
    {
        using var budget = new CancellationTokenSource(BundleBudget(cursor));

        try
        {
            await bundleWriter.BuildBundleAsync(_ctx.Context.SessionId, _ctx.Context.SessionDirectory, cursor.InstallationId, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge, OctopusRestOptions.ChunkSecondsEnvVar);
        }
    }

    // Every document and task detail the fetch committed must still be stored before anything is bundled; fewer means data was lost and
    // nothing is imported. At least, not exactly: an item that failed part way may have left a valid document no count includes, and a
    // list re-read after it shrank stages nothing new, so what was committed is a lower bound of what is stored.
    private async Task EnsureStagingCompleteAsync(CancellationToken ct)
    {
        var cursor = _ctx.Cursor;
        var staged = await bundleWriter.CountStagedAsync(_ctx.Context.SessionId, ct).ConfigureAwait(false);

        foreach (var (kind, committed) in cursor.StagedDocumentCounts)
        {
            var stored = Enum.TryParse<OctopusResourceKind>(kind, out var parsed) ? staged.GetValueOrDefault(parsed) : 0;

            if (stored < committed)
                throw new OctopusImportHistoryStagingIncompleteException(kind, stored, committed);
        }

        var details = await detailsStore.CountAsync(_ctx.Context.SessionId, ct).ConfigureAwait(false);

        if (details < cursor.StagedDetails)
            throw new OctopusImportHistoryStagingIncompleteException("TaskDetails", details, cursor.StagedDetails);
    }

    // A cursor without a lease (no claim recorded) gets what any chunk gets.
    private TimeSpan BundleBudget(OctopusImportHistoryFetchDto cursor)
    {
        if (cursor.LeaseExpiresAt is not { } lease) return _options.ChunkDeadline;

        var left = lease - DateTimeOffset.UtcNow - BundleLeaseSafety;

        return left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
    }

    // What is stored is capped (staged documents by the bundle writer, task details here), not what was read: snapshots read for
    // feed ids, task details, reads ahead a chunk discards and re-reads after an abandoned chunk are never staged. One chunk's
    // reads stay bounded, so a hostile server cannot stream without end.
    private void TrackBytes()
    {
        _ctx.Cursor.TotalBytes = _ctx.BytesBeforeChunk + _ctx.Client.BytesRead;

        if (_ctx.Client.BytesRead > _limits.MaxBytes || _ctx.Cursor.DetailsBytes > _limits.MaxBytes)
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge, OctopusImportSizeLimits.MaxBytesEnvVar);
    }

    private sealed class FetchRun(OctopusHistoryFetchContext context, OctopusRestClient client, Stopwatch clock, long bytesBeforeChunk)
    {
        // Inputs
        public OctopusHistoryFetchContext Context { get; } = context;
        public OctopusImportHistoryFetchDto Cursor { get; } = context.Cursor;
        public OctopusRestClient Client { get; } = client;
        public Stopwatch Clock { get; } = clock;
        public long BytesBeforeChunk { get; } = bytesBeforeChunk;

        // Per-chunk caches
        public ConcurrentDictionary<string, Lazy<Task<OctopusDeploymentProcessDto>>> ProcessSnapshots { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, Lazy<Task<string>>> MachineNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        // The item being written: its counts and skipped requests join the cursor only when it commits
        public OctopusHistoryFetchCountsDto Pending { get; set; } = new();
        public List<OctopusHistoryFetchSkippedItemDto> PendingSkipped { get; set; } = [];
        public Dictionary<string, int> PendingStaged { get; set; } = [];
        public int PendingStagedDetails { get; set; }
        public int ItemsCommitted { get; set; }
        public bool InItem { get; set; }
        public object CurrentItem { get; set; }
        public string OctopusUserId { get; set; }
        public bool KeyIsNew { get; set; }
        public List<string> ClosureBatch { get; set; } = [];
    }

    // What an item's reads produced, handed to its write
    private sealed record ReleaseRead(OctopusReleaseDto Release, OctopusDeploymentProcessDto Process);

    private sealed record DeploymentRead(OctopusDeploymentDto Deployment, OctopusServerTaskDto Task, OctopusTaskHistoryDetails Details);
}
