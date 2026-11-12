using System.Security.Cryptography;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Merges an uploaded export with a REST-fetched history bundle into one extraction with one manifest. History
/// documents (Release, Deployment, ServerTask) from the bundle replace the export's document with the same id,
/// manifest entry included; everything else in the export is kept. Bundle paths are moved under
/// <see cref="BundlePathPrefix"/> so they can never collide with an export path.
/// </summary>
public static class OctopusImportSourceMerge
{
    public const string BundlePathPrefix = "octopus-history/";
    public const string MergedManifestPath = "manifest.json";

    private static readonly HashSet<OctopusDocumentKind> HistoryKinds = [OctopusDocumentKind.Release, OctopusDocumentKind.Deployment, OctopusDocumentKind.ServerTask];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static OctopusInputExtractionResult Merge(OctopusInputExtractionResult export, OctopusInputExtractionResult history)
    {
        var diagnostics = export.Diagnostics.Concat(history.Diagnostics).ToList();
        var exportManifest = SingleManifest(export);
        var historyManifest = SingleManifest(history);

        if (exportManifest == null || historyManifest == null)
            return new OctopusInputExtractionResult(export.Documents.Concat(history.Documents.Select(Rebase)).ToList(), diagnostics);

        var bundleEntries = historyManifest.Value.Manifest.Entries.Where(IsHistoryEntry).Select(RebaseEntry).ToList();
        var replacedIds = bundleEntries.Select(e => Normalize(e.Id)).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        var keptEntries = exportManifest.Value.Manifest.Entries.Where(e => !(IsHistoryEntry(e) && replacedIds.Contains(Normalize(e.Id)))).ToList();
        var manifest = MergedManifest(exportManifest.Value.Manifest, historyManifest.Value.Manifest, bundleEntries.Concat(keptEntries));

        return new OctopusInputExtractionResult(MergedDocuments(export, history, manifest), diagnostics);
    }

    private static List<OctopusExtractedJsonDocument> MergedDocuments(OctopusInputExtractionResult export, OctopusInputExtractionResult history, OctopusExportManifestDto manifest)
    {
        var listed = manifest.Entries.Select(e => NormalizePath(e.DocumentSource)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exportDocuments = export.Documents.Where(d => !IsManifest(d) && (listed.Contains(NormalizePath(d.SourcePath)) || !IsHistoryDocument(d)));
        var bundleDocuments = history.Documents.Where(d => !IsManifest(d)).Select(Rebase).Where(d => listed.Contains(NormalizePath(d.SourcePath)));

        return [ManifestDocument(manifest), .. bundleDocuments, .. exportDocuments];
    }

    private static OctopusExportManifestDto MergedManifest(OctopusExportManifestDto export, OctopusExportManifestDto history, IEnumerable<OctopusManifestEntryDto> entries)
        => new()
        {
            SchemaVersions = (export.SchemaVersions ?? []).Concat(history.SchemaVersions ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Entries = entries.DistinctBy(e => NormalizePath(e.DocumentSource), StringComparer.OrdinalIgnoreCase).ToList(),
            InstallationId = history.InstallationId ?? export.InstallationId,
            ExtensionData = export.ExtensionData ?? []
        };

    private static OctopusExtractedJsonDocument ManifestDocument(OctopusExportManifestDto manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);

        using var json = JsonDocument.Parse(bytes);

        return new OctopusExtractedJsonDocument(MergedManifestPath, OctopusDocumentClassifier.ClassifyFileName(MergedManifestPath), json.RootElement.Clone(), bytes.LongLength, Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant());
    }

    private static (OctopusExtractedJsonDocument Document, OctopusExportManifestDto Manifest)? SingleManifest(OctopusInputExtractionResult extraction)
    {
        var manifests = extraction.Documents.Where(IsManifest).ToList();

        if (manifests.Count != 1) return null;

        try
        {
            var manifest = manifests[0].Root.Deserialize<OctopusExportManifestDto>(JsonOptions);

            return manifest == null ? null : (manifests[0], manifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsHistoryEntry(OctopusManifestEntryDto entry) => HistoryKinds.Contains(OctopusDocumentClassifier.Classify(entry).Kind);

    private static bool IsHistoryDocument(OctopusExtractedJsonDocument document) => HistoryKinds.Contains(document.Classification.Kind);

    private static bool IsManifest(OctopusExtractedJsonDocument document) => document.Classification.Kind == OctopusDocumentKind.Manifest;

    private static OctopusExtractedJsonDocument Rebase(OctopusExtractedJsonDocument document)
        => document with { SourcePath = BundlePathPrefix + NormalizePath(document.SourcePath), Classification = document.Classification with { SourcePath = BundlePathPrefix + NormalizePath(document.SourcePath) } };

    private static OctopusManifestEntryDto RebaseEntry(OctopusManifestEntryDto entry)
        => new() { Id = entry.Id, Name = entry.Name, DocumentType = entry.DocumentType, ExportType = entry.ExportType, DocumentSource = BundlePathPrefix + NormalizePath(entry.DocumentSource), ParentId = entry.ParentId, Hash = entry.Hash, ExtensionData = entry.ExtensionData };

    private static string Normalize(string id) => id?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string NormalizePath(string path) => string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').TrimStart('/');
}
