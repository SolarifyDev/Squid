using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.Rest;

public interface IOctopusHistoryBundleWriter : IScopedDependency
{
    /// <summary>Stages one fetched document; a document with the same kind and id overwrites the earlier copy.</summary>
    Task<long> WriteDocumentAsync(string sessionDirectory, OctopusResourceKind kind, OctopusDocumentDto document, CancellationToken ct = default);

    /// <summary>Packs every staged document into <c>octopus-history.zip</c> with a manifest and returns the path.</summary>
    Task<string> BuildBundleAsync(string sessionDirectory, string installationId, CancellationToken ct = default);
}

/// <summary>
/// Stages REST-fetched history documents under the session directory, keyed by a hash of (kind, id) so a page
/// read twice (the server's list shifted between chunks) overwrites rather than duplicates. The finished bundle
/// is an ordinary Octopus-shaped archive: a manifest whose SHA-1 hashes match each document, plus the documents.
/// </summary>
public sealed class OctopusHistoryBundleWriter : IOctopusHistoryBundleWriter
{
    public const string StagingDirectoryName = "history-staging";
    public const string DocumentFolder = "history";

    private static readonly OctopusResourceKind[] BundledKinds = [OctopusResourceKind.Release, OctopusResourceKind.Deployment, OctopusResourceKind.ServerTask];

    private static readonly JsonSerializerOptions DocumentOptions = new() { WriteIndented = false };

    public async Task<long> WriteDocumentAsync(string sessionDirectory, OctopusResourceKind kind, OctopusDocumentDto document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!BundledKinds.Contains(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only history documents are bundled.");

        var path = StagedPath(sessionDirectory, kind, document.Id);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, document.GetType(), DocumentOptions);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);

        return bytes.LongLength;
    }

    public async Task<string> BuildBundleAsync(string sessionDirectory, string installationId, CancellationToken ct = default)
    {
        var bundlePath = Path.Combine(sessionDirectory, OctopusImportSourceReader.HistoryBundleFileName);
        var partialPath = bundlePath + ".partial";
        var documents = await ReadStagedDocumentsAsync(sessionDirectory, ct).ConfigureAwait(false);

        await WriteZipAsync(partialPath, BuildManifest(documents, installationId), documents, ct).ConfigureAwait(false);

        File.Move(partialPath, bundlePath, overwrite: true);

        return bundlePath;
    }

    public static string StagedPath(string sessionDirectory, OctopusResourceKind kind, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory))
            throw new ArgumentException("A session directory is required.", nameof(sessionDirectory));

        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("A fetched document needs an id.", nameof(sourceId));

        return Path.Combine(sessionDirectory, StagingDirectoryName, kind.ToString(), $"{FileKey(kind, sourceId)}.json");
    }

    private static string FileKey(OctopusResourceKind kind, string sourceId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{sourceId.Trim().ToUpperInvariant()}"))).ToLowerInvariant();

    private static async Task<List<StagedDocument>> ReadStagedDocumentsAsync(string sessionDirectory, CancellationToken ct)
    {
        var documents = new List<StagedDocument>();

        foreach (var kind in BundledKinds)
        {
            var directory = Path.Combine(sessionDirectory, StagingDirectoryName, kind.ToString());

            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                documents.Add(ToStagedDocument(kind, file, await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false)));
        }

        return documents;
    }

    private static StagedDocument ToStagedDocument(OctopusResourceKind kind, string file, byte[] content)
    {
        using var json = JsonDocument.Parse(content);

        var id = ReadString(json.RootElement, "Id");
        var name = kind == OctopusResourceKind.Release ? ReadString(json.RootElement, "Version") ?? ReadString(json.RootElement, "Name") : ReadString(json.RootElement, "Name");

        return new StagedDocument(kind, id, name, $"{DocumentFolder}/{kind}/{Path.GetFileName(file)}", content);
    }

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static OctopusExportManifestDto BuildManifest(IEnumerable<StagedDocument> documents, string installationId)
        => new()
        {
            InstallationId = installationId,
            Entries = documents.Select(d => new OctopusManifestEntryDto { Id = d.Id, Name = d.Name, DocumentType = d.Kind.ToString(), ExportType = "FullDocument", DocumentSource = d.EntryPath, Hash = Sha1(d.Content) }).ToList()
        };

    private static string Sha1(byte[] content) => Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();

    private static async Task WriteZipAsync(string path, OctopusExportManifestDto manifest, IEnumerable<StagedDocument> documents, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);

        await WriteEntryAsync(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, DocumentOptions), ct).ConfigureAwait(false);

        foreach (var document in documents)
            await WriteEntryAsync(zip, document.EntryPath, document.Content, ct).ConfigureAwait(false);
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string entryPath, byte[] content, CancellationToken ct)
    {
        await using var entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal).Open();

        await entry.WriteAsync(content, ct).ConfigureAwait(false);
    }

    private sealed record StagedDocument(OctopusResourceKind Kind, string Id, string Name, string EntryPath, byte[] Content);
}
