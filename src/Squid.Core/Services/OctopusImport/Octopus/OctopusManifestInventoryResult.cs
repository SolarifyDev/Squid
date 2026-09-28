namespace Squid.Core.Services.OctopusImport.Octopus;

public sealed record OctopusManifestInventoryResult(
    OctopusExportManifestDto Manifest,
    IReadOnlyList<OctopusManifestInventoryItem> Items,
    IReadOnlyList<OctopusManifestInventoryCount> Counts,
    IReadOnlyList<OctopusInputExtractionDiagnostic> Diagnostics)
{
    public bool HasManifest => Manifest != null;
}

public sealed record OctopusManifestInventoryItem(
    OctopusManifestEntryDto ManifestEntry,
    OctopusExtractedJsonDocument Document,
    OctopusDocumentClassification Classification,
    bool HashMatches)
{
    public bool HasDocument => Document != null;
}

public sealed record OctopusManifestInventoryCount(OctopusDocumentKind Kind, int Count);
