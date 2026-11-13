using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

public interface IOctopusHistoryBundleWriter : IScopedDependency
{
    /// <summary>Stages one fetched document; a document with the same kind and id overwrites the earlier copy.</summary>
    Task<long> WriteDocumentAsync(string sessionDirectory, OctopusResourceKind kind, OctopusDocumentDto document, CancellationToken ct = default);

    /// <summary>Stages one export-shaped JSON document (the output of <see cref="OctopusRestDocumentNormalizer"/>); same overwrite rule.</summary>
    Task<long> WriteJsonAsync(string sessionDirectory, OctopusResourceKind kind, JsonObject document, CancellationToken ct = default);

    /// <summary>Whether a document of this kind and id is staged.</summary>
    bool IsStaged(string sessionDirectory, OctopusResourceKind kind, string sourceId);

    /// <summary>A staged document, or null when none is staged under this kind and id.</summary>
    Task<JsonObject> ReadStagedAsync(string sessionDirectory, OctopusResourceKind kind, string sourceId, CancellationToken ct = default);

    /// <summary>Packs every staged document into <c>octopus-history.zip</c> with a manifest and returns the path.</summary>
    Task<string> BuildBundleAsync(string sessionDirectory, string installationId, CancellationToken ct = default);
}

/// <summary>
/// Stages REST-fetched documents under the session directory, keyed by a hash of (kind, id) so a page read twice (the
/// server's list shifted between chunks) overwrites rather than duplicates. Staged bytes and documents are counted against
/// <see cref="OctopusImportSizeLimits"/> on every write, so a fetch fails at the document that crosses a limit instead of
/// producing a bundle the extractor would later refuse. The finished bundle is an ordinary Octopus-shaped archive: a
/// manifest whose SHA-1 hashes match each document, plus the documents, with fixed entry times so the same staged
/// documents always produce the same bytes.
/// </summary>
public sealed class OctopusHistoryBundleWriter(OctopusImportSizeLimits limits = null) : IOctopusHistoryBundleWriter
{
    public const string StagingDirectoryName = "history-staging";
    public const string DocumentFolder = "history";
    public const string TotalsFileName = "staging.totals";

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

    private readonly Dictionary<string, StagingTotals> _totals = new(StringComparer.Ordinal);

    public Task<long> WriteDocumentAsync(string sessionDirectory, OctopusResourceKind kind, OctopusDocumentDto document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        return StageAsync(sessionDirectory, kind, document.Id, JsonSerializer.SerializeToUtf8Bytes(document, document.GetType(), DocumentOptions), ct);
    }

    public Task<long> WriteJsonAsync(string sessionDirectory, OctopusResourceKind kind, JsonObject document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        return StageAsync(sessionDirectory, kind, ReadId(document), JsonSerializer.SerializeToUtf8Bytes(document, DocumentOptions), ct);
    }

    public bool IsStaged(string sessionDirectory, OctopusResourceKind kind, string sourceId)
        => !string.IsNullOrWhiteSpace(sourceId) && ManifestDocumentTypes.ContainsKey(kind) && File.Exists(StagedPath(sessionDirectory, kind, sourceId));

    public async Task<JsonObject> ReadStagedAsync(string sessionDirectory, OctopusResourceKind kind, string sourceId, CancellationToken ct = default)
    {
        if (!IsStaged(sessionDirectory, kind, sourceId)) return null;

        return JsonNode.Parse(await File.ReadAllBytesAsync(StagedPath(sessionDirectory, kind, sourceId), ct).ConfigureAwait(false)) as JsonObject;
    }

    public async Task<string> BuildBundleAsync(string sessionDirectory, string installationId, CancellationToken ct = default)
    {
        var bundlePath = Path.Combine(sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName);
        var partialPath = bundlePath + ".partial";
        var documents = await ReadStagedDocumentsAsync(sessionDirectory, ct).ConfigureAwait(false);
        var manifest = SerializeManifest(documents, installationId);

        await WriteZipAsync(partialPath, manifest, documents, ct).ConfigureAwait(false);

        File.Move(partialPath, bundlePath, overwrite: true);

        return bundlePath;
    }

    public static string StagedPath(string sessionDirectory, OctopusResourceKind kind, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory))
            throw new ArgumentException("A session directory is required.", nameof(sessionDirectory));

        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("A fetched document needs an id.", nameof(sourceId));

        return Path.Combine(StagingDirectory(sessionDirectory), kind.ToString(), $"{FileKey(kind, sourceId)}.json");
    }

    // ── Staging ─────────────────────────────────────────────────────────────

    private async Task<long> StageAsync(string sessionDirectory, OctopusResourceKind kind, string sourceId, byte[] content, CancellationToken ct)
    {
        EnsureStageable(kind);

        var path = StagedPath(sessionDirectory, kind, sourceId);
        var totals = await LoadTotalsAsync(sessionDirectory, ct).ConfigureAwait(false);
        var next = totals.Replace(PreviousLength(path), content.LongLength);

        EnsureWithinLimits(content.LongLength, next);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllBytesAsync(path, content, ct).ConfigureAwait(false);
        await SaveTotalsAsync(sessionDirectory, next, ct).ConfigureAwait(false);

        return content.LongLength;
    }

    private static void EnsureStageable(OctopusResourceKind kind)
    {
        if (!ManifestDocumentTypes.ContainsKey(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only whole Octopus documents are bundled; child resources travel inside their parent document.");
    }

    private void EnsureWithinLimits(long documentBytes, StagingTotals next)
    {
        if (documentBytes > _limits.MaxDocumentBytes)
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge, $"one document may hold at most {_limits.MaxDocumentBytes} bytes");

        if (next.Documents > _limits.MaxDocumentCount)
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge, $"at most {_limits.MaxDocumentCount} documents");

        if (next.Bytes > _limits.MaxBytes)
            throw new OctopusRestException(OctopusRestErrorKind.TooLarge, $"{OctopusImportSizeLimits.MaxBytesEnvVar} = {_limits.MaxBytes} bytes");
    }

    private static long? PreviousLength(string path) => File.Exists(path) ? new FileInfo(path).Length : null;

    private static string ReadId(JsonObject document)
        => document["Id"] is JsonValue id && id.TryGetValue<string>(out var value) ? value : throw new ArgumentException("A fetched document needs an id.", nameof(document));

    private static string StagingDirectory(string sessionDirectory) => Path.Combine(sessionDirectory, StagingDirectoryName);

    private static string TotalsPath(string sessionDirectory) => Path.Combine(StagingDirectory(sessionDirectory), TotalsFileName);

    private static string FileKey(OctopusResourceKind kind, string sourceId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{sourceId.Trim().ToUpperInvariant()}"))).ToLowerInvariant();

    // ── Totals: kept beside the staged documents so a later chunk (a new writer) continues the count ──

    private async Task<StagingTotals> LoadTotalsAsync(string sessionDirectory, CancellationToken ct)
    {
        if (_totals.TryGetValue(sessionDirectory, out var cached)) return cached;

        var totals = await ReadTotalsFileAsync(TotalsPath(sessionDirectory), ct).ConfigureAwait(false) ?? CountStagedDocuments(sessionDirectory);

        _totals[sessionDirectory] = totals;

        return totals;
    }

    private static async Task<StagingTotals> ReadTotalsFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<StagingTotals>(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // No totals file (staged before one existed, or lost): recount from disk, the source of truth.
    private static StagingTotals CountStagedDocuments(string sessionDirectory)
    {
        var files = StagedFiles(sessionDirectory).Select(f => new FileInfo(f.Path).Length).ToList();

        return new StagingTotals(files.Sum(), files.Count);
    }

    private async Task SaveTotalsAsync(string sessionDirectory, StagingTotals totals, CancellationToken ct)
    {
        var path = TotalsPath(sessionDirectory);

        await File.WriteAllBytesAsync(path + ".partial", JsonSerializer.SerializeToUtf8Bytes(totals), ct).ConfigureAwait(false);

        File.Move(path + ".partial", path, overwrite: true);

        _totals[sessionDirectory] = totals;
    }

    // ── Bundle ──────────────────────────────────────────────────────────────

    private static IEnumerable<(OctopusResourceKind Kind, string Path)> StagedFiles(string sessionDirectory)
    {
        foreach (var kind in ManifestDocumentTypes.Keys)
        {
            var directory = Path.Combine(StagingDirectory(sessionDirectory), kind.ToString());

            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                yield return (kind, file);
        }
    }

    // Reads one document at a time for its id, name and hash; the content is streamed into the zip later, never held.
    private static async Task<List<StagedDocument>> ReadStagedDocumentsAsync(string sessionDirectory, CancellationToken ct)
    {
        var documents = new List<StagedDocument>();

        foreach (var (kind, file) in StagedFiles(sessionDirectory))
            documents.Add(ToStagedDocument(kind, file, await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false)));

        return documents;
    }

    private static StagedDocument ToStagedDocument(OctopusResourceKind kind, string file, byte[] content)
    {
        using var json = JsonDocument.Parse(content);

        var id = ReadString(json.RootElement, "Id");
        var name = kind == OctopusResourceKind.Release ? ReadString(json.RootElement, "Version") ?? ReadString(json.RootElement, "Name") : ReadString(json.RootElement, "Name");

        return new StagedDocument(kind, id, name, $"{DocumentFolder}/{kind}/{Path.GetFileName(file)}", file, Sha1(content));
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

    private static async Task WriteZipAsync(string path, byte[] manifest, IEnumerable<StagedDocument> documents, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);

        await WriteEntryAsync(zip, "manifest.json", new MemoryStream(manifest), ct).ConfigureAwait(false);

        foreach (var document in documents)
            await WriteEntryAsync(zip, document.EntryPath, new FileStream(document.StagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true), ct).ConfigureAwait(false);
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string entryPath, Stream content, CancellationToken ct)
    {
        await using var source = content;
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);

        entry.LastWriteTime = EntryTimestamp;

        await using var destination = entry.Open();

        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    private sealed record StagedDocument(OctopusResourceKind Kind, string Id, string Name, string EntryPath, string StagedPath, string Hash);

    private sealed record StagingTotals(long Bytes, int Documents)
    {
        public StagingTotals Replace(long? previousBytes, long bytes)
            => previousBytes is { } previous ? this with { Bytes = Bytes - previous + bytes } : new StagingTotals(Bytes + bytes, Documents + 1);
    }
}
