namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// One source-map row to upsert at confirmation. <c>SourceType</c> is the Octopus resource kind name (or
/// <c>OctopusInstallation</c>); <c>SourceId</c> is normalised on write. <c>OutcomeState</c> is <c>Created</c> or <c>Reused</c>.
/// A <c>null</c> fingerprint keeps the stored one; <c>DetailsCleared</c> says the stored details are gone, so the
/// stored detail fingerprint is cleared instead of kept.
/// </summary>
public sealed record OctopusImportSourceMapRow(int SpaceId, string SourceType, string SourceId, int DestinationId, string OutcomeState, string Fingerprint = null, string DetailFingerprint = null)
{
    public bool DetailsCleared { get; init; }
}
