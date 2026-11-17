namespace Squid.Message.Models.OctopusImport;

/// <summary>The Octopus projects an import can choose from, grouped by project group, with the session's current selection.</summary>
public class OctopusImportProjectListDto
{
    public List<OctopusImportProjectGroupOptionDto> Groups { get; set; } = [];

    public List<OctopusImportProjectOptionDto> Projects { get; set; } = [];

    /// <summary>The session's current selection; null = whole space (nothing chosen yet, or an old client).</summary>
    public List<string> SelectedProjectIds { get; set; }
}

public class OctopusImportProjectGroupOptionDto
{
    public string Id { get; set; }

    public string Name { get; set; }
}

public class OctopusImportProjectOptionDto
{
    public string Id { get; set; }

    public string Name { get; set; }

    public string ProjectGroupId { get; set; }

    public bool IsDisabled { get; set; }

    /// <summary>An earlier import into this Squid space recorded this Octopus project (source map, not deleted in Squid). Advisory only: the preview decides.</summary>
    public bool PreviouslyImported { get; set; }

    /// <summary>The project is Octopus Config as Code (version-controlled): Squid does not import it, and the preview skips it with a reason.</summary>
    public bool IsConfigAsCode { get; set; }
}
