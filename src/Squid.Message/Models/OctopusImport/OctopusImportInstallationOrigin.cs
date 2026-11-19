namespace Squid.Message.Models.OctopusImport;

/// <summary>Where an earlier API import of a space came from: the Octopus installation, its server, space and version (no key).</summary>
public sealed record OctopusImportInstallationOrigin(string InstallationId, string ServerHost, string OctopusSpaceId, string OctopusVersion);
