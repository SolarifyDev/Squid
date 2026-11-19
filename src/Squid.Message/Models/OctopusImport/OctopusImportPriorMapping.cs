namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// A live Octopus → Squid mapping recorded by an earlier confirmed import. <c>CreatedInstallationId</c> / <c>CreatedExportSourceId</c>
/// name the origin of the import that created the row (both null: unknown, a row recorded before origins were kept per row).
/// </summary>
public sealed record OctopusImportPriorMapping(int DestinationId, string OutcomeState, string Fingerprint, string DetailFingerprint, string CreatedInstallationId = null, string CreatedExportSourceId = null);
