namespace Squid.Core.Services.OctopusImport.Octopus;

public sealed record OctopusDocumentClassification(
    OctopusDocumentKind Kind,
    string SourcePath,
    string SourceId,
    string ManifestDocumentType,
    bool IsHistoricalSnapshot,
    bool IsOutOfScopeHistory)
{
    public bool IsKnown => Kind != OctopusDocumentKind.Unknown;

    public bool IsCurrentConfiguration => IsKnown && !IsHistoricalSnapshot && !IsOutOfScopeHistory;
}
