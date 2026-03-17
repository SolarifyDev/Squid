using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Spaces;
using Squid.Message.Response;

namespace Squid.Message.Commands.Spaces;

[RequiresPermission(Permission.SpaceEdit)]
public class UpdateSpaceCommand : ICommand
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Slug { get; set; }
    public string Description { get; set; }
    public bool TaskQueueStopped { get; set; }
    public bool IsPrivate { get; set; }
}

public class UpdateSpaceResponse : SquidResponse<SpaceDto>
{
}
