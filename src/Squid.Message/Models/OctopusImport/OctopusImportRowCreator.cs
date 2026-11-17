namespace Squid.Message.Models.OctopusImport;

/// <summary>The origin of the import that created a source-map row: its Octopus installation, or else its export file's fingerprint.</summary>
public sealed record OctopusImportRowCreator(string InstallationId, string ExportSourceId);
