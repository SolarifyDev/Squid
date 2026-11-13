using System.Diagnostics;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher(IOctopusRestClientFactory clientFactory, IOctopusHistoryBundleWriter bundleWriter, IOctopusImportHistoryDetailsStore detailsStore, OctopusRestOptions options = null, OctopusImportSizeLimits limits = null) : IOctopusRestHistoryFetcher
{
    private const string ProjectIdPrefix = "PROJECTS-";

    /// <summary>How often the chunk deadline may abandon the same item before it is skipped.</summary>
    public const int MaxAbandonments = 3;

    /// <summary>The most skipped items the cursor lists; further ones are only counted.</summary>
    public const int MaxRecordedSkippedItems = 500;

    private static readonly string PriorProjectType = OctopusImportPriorMappings.Normalize(OctopusResourceKind.Project.ToString());

    private readonly OctopusRestOptions _options = options ?? OctopusRestOptions.FromEnvironment();
    private readonly OctopusImportSizeLimits _limits = limits ?? OctopusImportSizeLimits.FromEnvironment();

    private FetchRun _ctx;

    public async Task<OctopusServerProbeDto> ProbeAsync(string serverUrl, string apiKey, CancellationToken ct = default)
    {
        using var client = clientFactory.Create(serverUrl, apiKey);

        var root = await client.GetRootAsync(ct).ConfigureAwait(false);
        var spaces = root.Spaceless ? [] : await client.GetAsync<List<OctopusRestSpaceDto>>("api/spaces/all", ct).ConfigureAwait(false) ?? [];

        return new OctopusServerProbeDto { OctopusVersion = root.Version, InstallationId = root.InstallationId, Spaceless = root.Spaceless, Spaces = spaces.Select(s => new OctopusServerSpaceDto { Id = s.Id, Name = s.Name, IsDefault = s.IsDefault }).ToList() };
    }

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
        => new() { Phase = cursor.Phase, ProjectIndex = cursor.ProjectIndex, ProjectCount = cursor.ProjectIds.Count, Counts = cursor.Counts, TotalBytes = cursor.TotalBytes, Done = cursor.Done };

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
        await BindKeyAsync(ct).ConfigureAwait(false);
        await ResolveSpaceAsync(ct).ConfigureAwait(false);
        EnsureScope();
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

    // Every chunk asks Octopus whose key this is: a key of another user cannot continue a fetch it did not start.
    private async Task BindKeyAsync(CancellationToken ct)
    {
        var user = await _ctx.Client.GetCurrentUserAsync(ct).ConfigureAwait(false);

        OctopusRestKeyBinding.Bind(_ctx.Cursor, user.Id, _ctx.Client.KeyFingerprint);
    }

    private async Task ResolveSpaceAsync(CancellationToken ct)
    {
        var cursor = _ctx.Cursor;

        if (cursor.Spaceless || !string.IsNullOrWhiteSpace(cursor.OctopusSpaceId)) return;

        var spaces = await _ctx.Client.GetAsync<List<OctopusRestSpaceDto>>("api/spaces/all", ct).ConfigureAwait(false) ?? [];

        cursor.OctopusSpaceId = spaces.FirstOrDefault(s => s.IsDefault)?.Id ?? throw new OctopusRestException(OctopusRestErrorKind.NotFound);
    }

    private void EnsureScope()
    {
        var cursor = _ctx.Cursor;

        if (cursor.ProjectIds.Count == 0) cursor.ProjectIds = ScopeProjectIds(_ctx.Context, cursor.InstallationId);

        if (cursor.ProjectIds.Count == 0)
            throw new OctopusImportHistoryScopeEmptyException();
    }

    /// <summary>The projects whose history is fetched: those in an uploaded export plus those imported before from this installation.</summary>
    public static List<string> ScopeProjectIds(OctopusHistoryFetchContext context, string installationId)
    {
        var prior = context.PriorMappings ?? OctopusImportPriorMappings.Empty;
        var priorProjects = IsSameInstallation(prior.InstallationId, installationId) ? prior.Mappings.Keys.Where(k => k.Type == PriorProjectType).Select(k => OctopusProjectId(k.Id)) : [];

        return (context.ExportProjectIds ?? []).Select(OctopusImportSourceIds.StripExportSuffix).Concat(priorProjects).Where(id => !string.IsNullOrWhiteSpace(id)).DistinctBy(OctopusImportPriorMappings.Normalize).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The source map stores ids upper-cased; Octopus project ids are "Projects-{n}", so restore that spelling for the URL.
    private static string OctopusProjectId(string normalizedId)
        => normalizedId.StartsWith(ProjectIdPrefix, StringComparison.OrdinalIgnoreCase) ? "Projects-" + normalizedId[ProjectIdPrefix.Length..] : normalizedId;

    private static bool IsSameInstallation(string prior, string current) => string.IsNullOrWhiteSpace(prior) || OctopusImportPriorMappings.Normalize(prior) == OctopusImportPriorMappings.Normalize(current);

    private async Task FetchPagesAsync(CancellationToken ct)
    {
        _ctx.FetchingItems = true;

        do
        {
            await FetchNextPageAsync(ct).ConfigureAwait(false);
            TrackBytes();
        }
        while (!_ctx.Cursor.Done && _ctx.Clock.Elapsed < _options.ChunkDuration);
    }

    private async Task FetchNextPageAsync(CancellationToken ct)
    {
        var cursor = _ctx.Cursor;

        if (cursor.ProjectIndex >= cursor.ProjectIds.Count)
        {
            await CompleteAsync(ct).ConfigureAwait(false);
            return;
        }

        if (cursor.Phase == OctopusHistoryFetchPhase.Deployments)
            await FetchDeploymentPageAsync(ct).ConfigureAwait(false);
        else
            await FetchReleasePageAsync(ct).ConfigureAwait(false);
    }

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
        public Dictionary<string, string> MachineNames { get; set; }

        // The item being written: its counts and skipped requests join the cursor only when it commits
        public OctopusHistoryFetchCountsDto Pending { get; set; } = new();
        public List<OctopusHistoryFetchSkippedItemDto> PendingSkipped { get; set; } = [];
        public int ItemsCommitted { get; set; }
        public bool FetchingItems { get; set; }
    }
}
