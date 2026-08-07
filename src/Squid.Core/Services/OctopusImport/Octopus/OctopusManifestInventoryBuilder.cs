using System.Text.Json;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Octopus;

public interface IOctopusManifestInventoryBuilder : IScopedDependency
{
    OctopusManifestInventoryResult Build(OctopusInputExtractionResult extractionResult);
}

public class OctopusManifestInventoryBuilder : IOctopusManifestInventoryBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OctopusManifestInventoryResult Build(OctopusInputExtractionResult extractionResult)
    {
        ArgumentNullException.ThrowIfNull(extractionResult);

        var diagnostics = new List<OctopusInputExtractionDiagnostic>(extractionResult.Diagnostics);
        var manifestDocuments = extractionResult.Documents
            .Where(d => d.Classification.Kind == OctopusDocumentKind.Manifest)
            .ToList();

        if (manifestDocuments.Count == 0)
        {
            diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.ManifestMissing,
                "Octopus import input does not contain manifest.json."));

            return new OctopusManifestInventoryResult(null, [], [], diagnostics);
        }

        if (manifestDocuments.Count > 1)
        {
            diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.MultipleManifests,
                "Octopus import input contains multiple manifest documents.",
                manifestDocuments[1].SourcePath));
        }

        var manifestDocument = SelectManifest(manifestDocuments);
        var manifest = DeserializeManifest(manifestDocument, diagnostics);

        if (manifest == null)
            return new OctopusManifestInventoryResult(null, [], [], diagnostics);

        var documentsByPath = extractionResult.Documents
            .Where(d => d.Classification.Kind != OctopusDocumentKind.Manifest)
            .GroupBy(d => NormalizePath(d.SourcePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var entriesBySource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<OctopusManifestInventoryItem>();

        foreach (var entry in manifest.Entries)
        {
            var manifestClassification = OctopusDocumentClassifier.Classify(entry);
            var sourcePath = NormalizePath(entry.DocumentSource);

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                diagnostics.Add(Blocker(
                    OctopusInputExtractionDiagnosticCodes.ManifestEntryMissingSource,
                    "Octopus import manifest entry is missing DocumentSource.",
                    sourceId: entry.Id,
                    documentKind: manifestClassification.Kind));

                items.Add(new OctopusManifestInventoryItem(entry, null, manifestClassification, false));
                continue;
            }

            if (!entriesBySource.Add(sourcePath))
            {
                diagnostics.Add(Blocker(
                    OctopusInputExtractionDiagnosticCodes.ManifestDuplicateSource,
                    $"Octopus import manifest lists document '{sourcePath}' more than once.",
                    sourcePath,
                    entry.Id,
                    manifestClassification.Kind));
            }

            if (!documentsByPath.TryGetValue(sourcePath, out var document))
            {
                diagnostics.Add(Blocker(
                    OctopusInputExtractionDiagnosticCodes.ManifestDocumentMissing,
                    $"Octopus import manifest references missing document '{sourcePath}'.",
                    sourcePath,
                    entry.Id,
                    manifestClassification.Kind));

                items.Add(new OctopusManifestInventoryItem(entry, null, manifestClassification, false));
                continue;
            }

            ValidateManifestEntryAgainstDocument(entry, manifestClassification, document, diagnostics);

            var hashMatches = ValidateHash(entry, document, diagnostics);
            items.Add(new OctopusManifestInventoryItem(entry, document, manifestClassification, hashMatches));
        }

        AddUnlistedDocumentDiagnostics(documentsByPath.Values, entriesBySource, diagnostics);

        var counts = items
            .GroupBy(i => i.Classification.Kind)
            .OrderBy(g => g.Key)
            .Select(g => new OctopusManifestInventoryCount(g.Key, g.Count()))
            .ToList();

        return new OctopusManifestInventoryResult(manifest, items, counts, diagnostics);
    }

    private static OctopusExtractedJsonDocument SelectManifest(List<OctopusExtractedJsonDocument> manifestDocuments)
    {
        return manifestDocuments.FirstOrDefault(d => string.Equals(Path.GetFileName(d.SourcePath), "manifest.json", StringComparison.OrdinalIgnoreCase))
               ?? manifestDocuments[0];
    }

    private static OctopusExportManifestDto DeserializeManifest(
        OctopusExtractedJsonDocument manifestDocument,
        List<OctopusInputExtractionDiagnostic> diagnostics)
    {
        try
        {
            return manifestDocument.Root.Deserialize<OctopusExportManifestDto>(JsonOptions);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.ManifestMalformed,
                $"Octopus import manifest '{manifestDocument.SourcePath}' could not be deserialized ({ex.GetType().Name}).",
                manifestDocument.SourcePath));

            return null;
        }
    }

    private static void ValidateManifestEntryAgainstDocument(
        OctopusManifestEntryDto entry,
        OctopusDocumentClassification manifestClassification,
        OctopusExtractedJsonDocument document,
        List<OctopusInputExtractionDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(entry.Id) &&
            !string.IsNullOrWhiteSpace(document.Classification.SourceId) &&
            !string.Equals(entry.Id, document.Classification.SourceId, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.ManifestSourceIdMismatch,
                $"Octopus import manifest entry for '{NormalizePath(entry.DocumentSource)}' does not match the parsed document id.",
                NormalizePath(entry.DocumentSource),
                entry.Id,
                manifestClassification.Kind));
        }

        if (manifestClassification.Kind != document.Classification.Kind)
        {
            diagnostics.Add(Warning(
                OctopusInputExtractionDiagnosticCodes.ManifestDocumentTypeMismatch,
                $"Octopus import manifest entry for '{NormalizePath(entry.DocumentSource)}' has a document type that differs from parsed document classification.",
                NormalizePath(entry.DocumentSource),
                entry.Id,
                manifestClassification.Kind));
        }
    }

    private static bool ValidateHash(
        OctopusManifestEntryDto entry,
        OctopusExtractedJsonDocument document,
        List<OctopusInputExtractionDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(entry.Hash))
            return false;

        if (string.Equals(entry.Hash, document.Sha1, StringComparison.OrdinalIgnoreCase))
            return true;

        diagnostics.Add(Blocker(
            OctopusInputExtractionDiagnosticCodes.ManifestHashMismatch,
            $"Octopus import manifest hash does not match document '{NormalizePath(entry.DocumentSource)}'.",
            NormalizePath(entry.DocumentSource),
            entry.Id,
            document.Classification.Kind));

        return false;
    }

    private static void AddUnlistedDocumentDiagnostics(
        IEnumerable<OctopusExtractedJsonDocument> documents,
        HashSet<string> listedPaths,
        List<OctopusInputExtractionDiagnostic> diagnostics)
    {
        foreach (var document in documents.OrderBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var sourcePath = NormalizePath(document.SourcePath);

            if (listedPaths.Contains(sourcePath))
                continue;

            diagnostics.Add(Warning(
                OctopusInputExtractionDiagnosticCodes.DocumentNotInManifest,
                $"Octopus import input contains JSON document '{sourcePath}' that is not listed in the manifest.",
                sourcePath,
                document.Classification.SourceId,
                document.Classification.Kind));
        }
    }

    private static string NormalizePath(string path)
        => string.IsNullOrWhiteSpace(path) ? path : path.Replace('\\', '/').TrimStart('/');

    private static OctopusInputExtractionDiagnostic Warning(
        string code,
        string message,
        string sourcePath = null,
        string sourceId = null,
        OctopusDocumentKind? documentKind = null)
        => new(OctopusImportCompatibilitySeverity.Warning, code, message, sourcePath, sourceId, documentKind);

    private static OctopusInputExtractionDiagnostic Blocker(
        string code,
        string message,
        string sourcePath = null,
        string sourceId = null,
        OctopusDocumentKind? documentKind = null)
        => new(OctopusImportCompatibilitySeverity.Blocker, code, message, sourcePath, sourceId, documentKind);
}
