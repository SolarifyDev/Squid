using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SessionFiles;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public interface IOctopusHistoryBundleWriter : IScopedDependency
{
    /// <summary>Stages one fetched document; a document with the same kind and id overwrites the earlier copy.</summary>
    Task<OctopusImportSessionFileWrite> WriteDocumentAsync(Guid sessionId, OctopusResourceKind kind, OctopusDocumentDto document, CancellationToken ct = default);

    /// <summary>Stages one export-shaped JSON document (the output of <see cref="OctopusRestDocumentNormalizer"/>); same overwrite rule.</summary>
    Task<OctopusImportSessionFileWrite> WriteJsonAsync(Guid sessionId, OctopusResourceKind kind, JsonObject document, CancellationToken ct = default);

    /// <summary>Whether a document of this kind and id is staged.</summary>
    Task<bool> IsStagedAsync(Guid sessionId, OctopusResourceKind kind, string sourceId, CancellationToken ct = default);

    /// <summary>A staged document, or null when none is staged under this kind and id.</summary>
    Task<JsonObject> ReadStagedAsync(Guid sessionId, OctopusResourceKind kind, string sourceId, CancellationToken ct = default);

    /// <summary>How many documents of each kind are staged.</summary>
    Task<IReadOnlyDictionary<OctopusResourceKind, int>> CountStagedAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Packs every staged document into <c>octopus-history.zip</c> in <paramref name="sessionDirectory"/> with a manifest, stores it with the session and returns its path.</summary>
    Task<string> BuildBundleAsync(Guid sessionId, string sessionDirectory, string installationId, CancellationToken ct = default);
}

/// <summary>
/// Stages REST-fetched documents with the session (<see cref="IOctopusImportSessionFileStore"/>, so any replica continues a fetch), keyed
/// by a hash of (kind, id) so a page read twice (the server's list shifted between chunks) overwrites rather than duplicates. Staged
/// bytes and documents are counted against <see cref="OctopusImportSizeLimits"/> on every write, so a fetch fails at the document that
/// crosses a limit instead of producing a bundle the extractor would later refuse. The finished bundle is an ordinary Octopus-shaped
/// archive: a manifest whose SHA-1 hashes match each document, plus the documents, with fixed entry times so the same staged documents
/// always produce the same bytes. It is built only from every staged document: one that disappears while it is built fails the build.
/// </summary>
public sealed class OctopusHistoryBundleWriter(IOctopusImportSessionFileStore files, OctopusImportSizeLimits limits = null) : IOctopusHistoryBundleWriter
{
    public const string DocumentFolder = "history";

    /// <summary>The manifest <c>DocumentType</c> written for each stageable kind; snapshots use their parent type (the id marks them).</summary>
    public static IReadOnlyDictionary<OctopusResourceKind, string> ManifestDocumentTypes { get; } = new Dictionary<OctopusResourceKind, string>
    {
        [OctopusResourceKind.ProjectGroup] = "ProjectGroup",
        [OctopusResourceKind.Environment] = "Environment",
        [OctopusResourceKind.Lifecycle] = "Lifecycle",
        [OctopusResourceKind.Feed] = "Feed",
        [OctopusResourceKind.Account] = "Account",
        [OctopusResourceKind.Team] = "Team",
        [OctopusResourceKind.LibraryVariableSet] = "LibraryVariableSet",
        [OctopusResourceKind.VariableSet] = "VariableSet",
        [OctopusResourceKind.VariableSetSnapshot] = "VariableSet",
        [OctopusResourceKind.ActionTemplate] = "ActionTemplate",
        [OctopusResourceKind.Machine] = "Machine",
        [OctopusResourceKind.Certificate] = "Certificate",
        [OctopusResourceKind.WorkerPool] = "WorkerPool",
        [OctopusResourceKind.Tenant] = "Tenant",
        [OctopusResourceKind.Project] = "Project",
        [OctopusResourceKind.Channel] = "Channel",
        [OctopusResourceKind.DeploymentProcess] = "DeploymentProcess",
        [OctopusResourceKind.DeploymentProcessSnapshot] = "DeploymentProcess",
        [OctopusResourceKind.DeploymentSettings] = "DeploymentSettings",
        [OctopusResourceKind.Runbook] = "Runbook",
        [OctopusResourceKind.Trigger] = "Trigger",
        [OctopusResourceKind.Release] = "Release",
        [OctopusResourceKind.Deployment] = "Deployment",
        [OctopusResourceKind.ServerTask] = "ServerTask"
    };

    private static readonly DateTimeOffset EntryTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions DocumentOptions = new() { WriteIndented = false };

    private readonly OctopusImportSizeLimits _limits = limits ?? OctopusImportSizeLimits.FromEnvironment();

    private readonly Dictionary<Guid, StagingTotals> _totals = new();

    // Paths staged per kind, loaded once for the kinds whose references are checked (Channel, Environment, Feed, Team: all small).
    private readonly Dictionary<(Guid, OctopusResourceKind), HashSet<string>> _stagedPaths = new();

    public Task<OctopusImportSessionFileWrite> WriteDocumentAsync(Guid sessionId, OctopusResourceKind kind, OctopusDocumentDto document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        return StageAsync(sessionId, kind, document.Id, JsonSerializer.SerializeToUtf8Bytes(document, document.GetType(), DocumentOptions), ct);
    }

    public Task<OctopusImportSessionFileWrite> WriteJsonAsync(Guid sessionId, OctopusResourceKind kind, JsonObject document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        return StageAsync(sessionId, kind, ReadId(document), JsonSerializer.SerializeToUtf8Bytes(document, DocumentOptions), ct);
    }

    public async Task<bool> IsStagedAsync(Guid sessionId, OctopusResourceKind kind, string sourceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || !ManifestDocumentTypes.ContainsKey(kind)) return false;

        return (await StagedPathsAsync(sessionId, kind, ct).ConfigureAwait(false)).Contains(StagedPath(kind, sourceId));
    }

    public async Task<JsonObject> ReadStagedAsync(Guid sessionId, OctopusResourceKind kind, string sourceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || !ManifestDocumentTypes.ContainsKey(kind)) return null;

        var content = await files.ReadAsync(sessionId, StagedPath(kind, sourceId), ct).ConfigureAwait(false);

        return content == null ? null : JsonNode.Parse(content) as JsonObject;
    }

    public async Task<IReadOnlyDictionary<OctopusResourceKind, int>> CountStagedAsync(Guid sessionId, CancellationToken ct = default)
    {
        var headers = await files.ListAsync(sessionId, OctopusImportSessionFilePaths.StagingPrefix, ct).ConfigureAwait(false);

        return ManifestDocumentTypes.Keys.ToDictionary(kind => kind, kind => headers.Count(h => h.Path.StartsWith(KindPrefix(kind), StringComparison.Ordinal)));
    }

    public async Task<string> BuildBundleAsync(Guid sessionId, string sessionDirectory, string installationId, CancellationToken ct = default)
    {
        var bundlePath = Path.Combine(sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName);
        var partialPath = bundlePath + ".partial";
        var staged = await CountStagedAsync(sessionId, ct).ConfigureAwait(false);

        Directory.CreateDirectory(sessionDirectory);

        await WriteZipAsync(partialPath, installationId, sessionId, staged, ct).ConfigureAwait(false);

        File.Move(partialPath, bundlePath, overwrite: true);

        await StoreBundleAsync(sessionId, bundlePath, ct).ConfigureAwait(false);

        return bundlePath;
    }

    /// <summary>The stored path of a staged document: <c>history-staging/{Kind}/{hash of kind and id}.json</c>.</summary>
    public static string StagedPath(OctopusResourceKind kind, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("A fetched document needs an id.", nameof(sourceId));

        return $"{KindPrefix(kind)}{FileKey(kind, sourceId)}.json";
    }

    public static string KindPrefix(OctopusResourceKind kind) => $"{OctopusImportSessionFilePaths.StagingPrefix}{kind}/";

    // ── Staging ─────────────────────────────────────────────────────────────

    private async Task<OctopusImportSessionFileWrite> StageAsync(Guid sessionId, OctopusResourceKind kind, string sourceId, byte[] content, CancellationToken ct)
    {
        EnsureStageable(kind);

        var path = StagedPath(kind, sourceId);
        var totals = await LoadTotalsAsync(sessionId, ct).ConfigureAwait(false);

        await EnsureWithinLimitsAsync(sessionId, path, content.LongLength, totals, ct).ConfigureAwait(false);

        var write = await files.WriteAsync(sessionId, path, new MemoryStream(content, writable: false), OctopusImportSessionFileEncoding.Deflate, ct).ConfigureAwait(false);

        _totals[sessionId] = totals.Replace(write.PreviousLength, write.Length);
        RememberStaged(sessionId, kind, path);

        return write;
    }

    private static void EnsureStageable(OctopusResourceKind kind)
    {
        if (!ManifestDocumentTypes.ContainsKey(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only whole Octopus documents are bundled; child resources travel inside their parent document.");
    }

    // Counted as a new document first; only a write that would cross a limit that way asks whether it replaces one, so the usual write costs nothing extra.
    private async Task EnsureWithinLimitsAsync(Guid sessionId, string path, long documentBytes, StagingTotals totals, CancellationToken ct)
    {
        if (documentBytes > _limits.MaxDocumentBytes)
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge, $"one document may hold at most {_limits.MaxDocumentBytes} bytes");

        if (IsWithinLimits(totals.Replace(null, documentBytes))) return;

        var previous = await files.GetHeaderAsync(sessionId, path, ct).ConfigureAwait(false);
        var next = totals.Replace(previous?.Length, documentBytes);

        if (next.Documents > _limits.MaxDocumentCount)
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge, $"at most {_limits.MaxDocumentCount} documents");

        if (next.Bytes > _limits.MaxBytes)
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge, $"{OctopusImportSizeLimits.MaxBytesEnvVar} = {_limits.MaxBytes} bytes");
    }

    private bool IsWithinLimits(StagingTotals next) => next.Documents <= _limits.MaxDocumentCount && next.Bytes <= _limits.MaxBytes;

    private static string ReadId(JsonObject document)
        => document["Id"] is JsonValue id && id.TryGetValue<string>(out var value) ? value : throw new ArgumentException("A fetched document needs an id.", nameof(document));

    private static string FileKey(OctopusResourceKind kind, string sourceId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{sourceId.Trim().ToUpperInvariant()}"))).ToLowerInvariant();

    // ── Totals and staged paths: read once per writer (one chunk), then kept current by its own writes ──

    private async Task<StagingTotals> LoadTotalsAsync(Guid sessionId, CancellationToken ct)
    {
        if (_totals.TryGetValue(sessionId, out var cached)) return cached;

        var stored = await files.TotalsAsync(sessionId, OctopusImportSessionFilePaths.StagingPrefix, ct).ConfigureAwait(false);

        return _totals[sessionId] = new StagingTotals(stored.Bytes, stored.Files);
    }

    private async Task<HashSet<string>> StagedPathsAsync(Guid sessionId, OctopusResourceKind kind, CancellationToken ct)
    {
        if (_stagedPaths.TryGetValue((sessionId, kind), out var cached)) return cached;

        var headers = await files.ListAsync(sessionId, KindPrefix(kind), ct).ConfigureAwait(false);

        return _stagedPaths[(sessionId, kind)] = headers.Select(h => h.Path).ToHashSet(StringComparer.Ordinal);
    }

    private void RememberStaged(Guid sessionId, OctopusResourceKind kind, string path)
    {
        if (_stagedPaths.TryGetValue((sessionId, kind), out var paths)) paths.Add(path);
    }

    // ── Bundle ──────────────────────────────────────────────────────────────

    private async Task StoreBundleAsync(Guid sessionId, string bundlePath, CancellationToken ct)
    {
        await using var bundle = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        await files.WriteAsync(sessionId, OctopusImportSourceReader.HistoryBundleFileName, bundle, OctopusImportSessionFileEncoding.Identity, ct).ConfigureAwait(false);
    }

    private static StagedDocument ToStagedDocument(OctopusResourceKind kind, string path, byte[] content)
    {
        using var json = JsonDocument.Parse(content);

        var id = ReadString(json.RootElement, "Id");
        var name = kind == OctopusResourceKind.Release ? ReadString(json.RootElement, "Version") ?? ReadString(json.RootElement, "Name") : ReadString(json.RootElement, "Name");

        return new StagedDocument(kind, id, name, $"{DocumentFolder}/{kind}/{path[KindPrefix(kind).Length..]}", Sha1(content));
    }

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private byte[] SerializeManifest(IEnumerable<StagedDocument> documents, string installationId)
    {
        var manifest = new OctopusExportManifestDto
        {
            InstallationId = installationId,
            Entries = documents.Select(d => new OctopusManifestEntryDto { Id = d.Id, Name = d.Name, DocumentType = ManifestDocumentTypes[d.Kind], ExportType = "FullDocument", DocumentSource = d.EntryPath, Hash = d.Hash }).ToList()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DocumentOptions);

        if (bytes.LongLength > _limits.MaxManifestBytes)
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge, $"the manifest may hold at most {_limits.MaxManifestBytes} bytes");

        return bytes;
    }

    private static string Sha1(byte[] content) => Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();

    // One staged document held at a time, read in each kind's path order: its id, name and hash come from the bytes written into the zip,
    // and the manifest, which needs every hash, is the last entry (readers find it by name). A bundle of a large space holds hundreds of
    // thousands of documents, so the fast compression level keeps bundling within one chunk's lease.
    private async Task WriteZipAsync(string path, string installationId, Guid sessionId, IReadOnlyDictionary<OctopusResourceKind, int> staged, CancellationToken ct)
    {
        var documents = new List<StagedDocument>();

        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);

        foreach (var kind in ManifestDocumentTypes.Keys)
            documents.AddRange(await WriteKindAsync(zip, sessionId, kind, staged[kind], ct).ConfigureAwait(false));

        await WriteEntryAsync(zip, "manifest.json", new MemoryStream(SerializeManifest(documents, installationId)), ct).ConfigureAwait(false);
    }

    // Every document counted before the build must be packed: one gone meanwhile (the session was cleaned up) fails the build, never a smaller bundle.
    private async Task<List<StagedDocument>> WriteKindAsync(ZipArchive zip, Guid sessionId, OctopusResourceKind kind, int expected, CancellationToken ct)
    {
        var documents = new List<StagedDocument>();

        await foreach (var staged in files.ReadAllAsync(sessionId, KindPrefix(kind), ct).ConfigureAwait(false))
            documents.Add(await WriteDocumentEntryAsync(zip, kind, staged, ct).ConfigureAwait(false));

        if (documents.Count != expected)
            throw new OctopusImportHistoryStagingIncompleteException(kind.ToString(), documents.Count, expected);

        return documents;
    }

    private static async Task<StagedDocument> WriteDocumentEntryAsync(ZipArchive zip, OctopusResourceKind kind, OctopusImportSessionFileContent staged, CancellationToken ct)
    {
        var document = ToStagedDocument(kind, staged.Path, staged.Content);

        await WriteEntryAsync(zip, document.EntryPath, new MemoryStream(staged.Content, writable: false), ct).ConfigureAwait(false);

        return document;
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string entryPath, Stream content, CancellationToken ct)
    {
        await using var source = content;
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Fastest);

        entry.LastWriteTime = EntryTimestamp;

        await using var destination = entry.Open();

        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    private sealed record StagedDocument(OctopusResourceKind Kind, string Id, string Name, string EntryPath, string Hash);

    private sealed record StagingTotals(long Bytes, int Documents)
    {
        public StagingTotals Replace(long? previousBytes, long bytes)
            => previousBytes is { } previous ? this with { Bytes = Bytes - previous + bytes } : new StagingTotals(Bytes + bytes, Documents + 1);
    }
}
