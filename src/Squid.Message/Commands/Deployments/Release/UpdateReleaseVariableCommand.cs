using Squid.Message.Attributes;
using Squid.Message.Enums;

namespace Squid.Message.Commands.Deployments.Release;

[RequiresPermission(Permission.ReleaseEdit)]
public class UpdateReleaseVariableCommand : ICommand
{
    public int ReleaseId { get; set; }
}