using System.Diagnostics;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher(IOctopusRestClientFactory clientFactory, IOctopusHistoryBundleWriter bundleWriter, IOctopusImportHistoryDetailsStore detailsStore, OctopusRestOptions options = null, OctopusImportSizeLimits limits = null) : IOctopusRestHistoryFetcher
{
    /// <summary>How often the chunk deadline may abandon the same item before it is skipped.</summary>
    public const int MaxAbandonments = 3;

    /// <summary>The most skipped items the cursor lists; further ones are only counted.</summary>
    public const int MaxRecordedSkippedItems = 500;

    private readonly OctopusRestOptions _options = options ?? OctopusRestOptions.FromEnvironment();
    private readonly OctopusImportSizeLimits _limits = limits ?? OctopusImportSizeLimits.FromEnvironment();

    private FetchRun _ctx;

    public async Task<OctopusHistoryFetchProgressDto> FetchChunkAsync(OctopusHistoryFetchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var client = clientFactory.Create(context.ServerUrl, context.ApiKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        deadline.CancelAfter(_options.ChunkDeadline);
        _ctx = new FetchRun(context, client, Stopwatch.StartNew(), context.Cursor.TotalBytes);
        client.ByteBudget = _limits.MaxBytes - _ctx.BytesBeforeChunk;

        await RunWithinDeadlineAsync(deadline, ct).ConfigureAwait(false);

        return Progress(_ctx.Cursor);
    }

    public static OctopusHistoryFetchProgressDto Progress(OctopusImportHistoryFetchDto cursor)
        => new() { Phase = cursor.Phase, ProjectIndex = cursor.ProjectIndex, ProjectCount = cursor.ProjectIds.Count, CatalogIndex = cursor.CatalogIndex, CatalogKindCount = CatalogCollections.Count, Counts = cursor.Counts, TotalBytes = cursor.TotalBytes, Done = cursor.Done };

    // A chunk still running past its deadline is abandoned before its lease lapses, so no second chunk can overlap it.
    // The item it was on is skipped once it has been abandoned MaxAbandonments times, so one stuck item cannot stall the fetch.
    private async Task RunWithinDeadlineAsync(CancellationTokenSource deadline, CancellationToken ct)
    {
        try
        {
            await RunChunkAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            if (!SkipIfAbandonedTooOften())
                throw new OctopusRestException(OctopusRestErrorKind.Unreachable);
        }
    }

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

    private async Task CompleteAsync(CancellationToken ct)
    {
        var cursor = _ctx.Cursor;

        await bundleWriter.BuildBundleAsync(_ctx.Context.SessionDirectory, cursor.InstallationId, ct).ConfigureAwait(false);

        cursor.Phase = OctopusHistoryFetchPhase.Done;
        cursor.Done = true;
        cursor.CompletedAt = DateTimeOffset.UtcNow;
    }

    private void TrackBytes()
    {
        _ctx.Cursor.TotalBytes = _ctx.BytesBeforeChunk + _ctx.Client.BytesRead;

        if (_ctx.Cursor.TotalBytes > _limits.MaxBytes)
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
        public Dictionary<string, OctopusDeploymentProcessDto> ProcessSnapshots { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> MachineNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        // The item being written: its counts and skipped requests join the cursor only when it commits
        public OctopusHistoryFetchCountsDto Pending { get; set; } = new();
        public List<OctopusHistoryFetchSkippedItemDto> PendingSkipped { get; set; } = [];
        public int ItemsCommitted { get; set; }
        public bool InItem { get; set; }
        public string OctopusUserId { get; set; }
        public bool KeyIsNew { get; set; }
        public List<string> ClosureBatch { get; set; } = [];
    }
}
