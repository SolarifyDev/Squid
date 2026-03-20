using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Spaces;

[RequiresPermission(Permission.SpaceDelete)]
public class DeleteSpaceCommand : ICommand
{
    public int Id { get; set; }
}

public class DeleteSpaceResponse : SquidResponse
{
}
