using System.Diagnostics;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public sealed partial class OctopusRestHistoryFetcher(IOctopusRestClientFactory clientFactory, IOctopusHistoryBundleWriter bundleWriter, IOctopusImportHistoryDetailsStore detailsStore, OctopusRestOptions options = null) : IOctopusRestHistoryFetcher
{
    private const string ProjectIdPrefix = "PROJECTS-";

    private static readonly string PriorProjectType = OctopusImportPriorMappings.Normalize(OctopusResourceKind.Project.ToString());

    private readonly OctopusRestOptions _options = options ?? OctopusRestOptions.FromEnvironment();

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

        _ctx = new FetchRun(context, client, Stopwatch.StartNew(), context.Cursor.TotalBytes);

        await PinInstallationAsync(ct).ConfigureAwait(false);
        await ResolveSpaceAsync(ct).ConfigureAwait(false);
        EnsureScope();
        await FetchPagesAsync(ct).ConfigureAwait(false);

        return Progress(_ctx.Cursor);
    }

    public static OctopusHistoryFetchProgressDto Progress(OctopusImportHistoryFetchDto cursor)
        => new() { Phase = cursor.Phase, ProjectIndex = cursor.ProjectIndex, ProjectCount = cursor.ProjectIds.Count, Counts = cursor.Counts, TotalBytes = cursor.TotalBytes, Done = cursor.Done };

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

        return (context.ExportProjectIds ?? []).Concat(priorProjects).Where(id => !string.IsNullOrWhiteSpace(id)).DistinctBy(OctopusImportPriorMappings.Normalize).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The source map stores ids upper-cased; Octopus project ids are "Projects-{n}", so restore that spelling for the URL.
    private static string OctopusProjectId(string normalizedId)
        => normalizedId.StartsWith(ProjectIdPrefix, StringComparison.OrdinalIgnoreCase) ? "Projects-" + normalizedId[ProjectIdPrefix.Length..] : normalizedId;

    private static bool IsSameInstallation(string prior, string current) => string.IsNullOrWhiteSpace(prior) || OctopusImportPriorMappings.Normalize(prior) == OctopusImportPriorMappings.Normalize(current);

    private async Task FetchPagesAsync(CancellationToken ct)
    {
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

        if (_ctx.Cursor.TotalBytes > _options.HistoryMaxBytes)
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge);
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
    }
}
