using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Commands.OctopusImport;

/// <summary>Chooses the Octopus projects a session reads and imports; only those and the shared resources they use are imported.</summary>
[RequiresPermission(Permission.ProjectCreate)]
public class SelectOctopusImportProjectsCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public Guid SessionId { get; set; }

    /// <summary>The chosen Octopus project ids; null imports the whole space.</summary>
    public List<string> ProjectIds { get; set; }
}

public class SelectOctopusImportProjectsResponse : SquidResponse<OctopusImportSessionDto>
{
}
