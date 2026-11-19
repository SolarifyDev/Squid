using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>The Squid project an Octopus project's changes land on. A client links to it by <see cref="ProjectId"/>.</summary>
public class OctopusImportProjectDestinationDto
{
    public OctopusImportProjectDestinationState State { get; set; }

    /// <summary>The existing Squid project; null for a new or not imported project.</summary>
    public int? ProjectId { get; set; }

    /// <summary>The existing Squid project's current name (it may differ from the Octopus name); null when unknown.</summary>
    public string ProjectName { get; set; }

    public string ProjectSlug { get; set; }
}
