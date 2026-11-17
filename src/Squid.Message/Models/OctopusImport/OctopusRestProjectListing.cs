namespace Squid.Message.Models.OctopusImport;

/// <summary>A space's projects and project groups as the Octopus API lists them, without any per-project request.</summary>
public sealed record OctopusRestProjectListing(string InstallationId, string SpaceId, IReadOnlyList<OctopusRestProjectGroupItem> Groups, IReadOnlyList<OctopusRestProjectItem> Projects);

public sealed record OctopusRestProjectGroupItem(string Id, string Name);

/// <param name="IsConfigAsCode">The project is Octopus Config as Code (version-controlled); Squid does not import it.</param>
public sealed record OctopusRestProjectItem(string Id, string Name, string ProjectGroupId, bool IsDisabled, bool IsConfigAsCode = false);
