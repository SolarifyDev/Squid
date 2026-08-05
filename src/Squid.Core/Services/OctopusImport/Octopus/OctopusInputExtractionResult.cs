using System.Text.Json;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Octopus;

public sealed record OctopusInputExtractionResult(
    IReadOnlyList<OctopusExtractedJsonDocument> Documents,
    IReadOnlyList<OctopusInputExtractionDiagnostic> Diagnostics)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}

public sealed record OctopusExtractedJsonDocument(
    string SourcePath,
    OctopusDocumentClassification Classification,
    JsonElement Root,
    long SizeBytes);

public sealed record OctopusInputExtractionDiagnostic(
    OctopusImportCompatibilitySeverity Severity,
    string Code,
    string Message,
    string SourcePath = null,
    string SourceId = null,
    OctopusDocumentKind? DocumentKind = null);
