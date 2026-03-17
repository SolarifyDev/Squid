using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Commands.Authorization;

[RequiresPermission(Permission.TeamEdit)]
public class UpdateRoleScopeCommand : ICommand
{
    public int TeamId { get; set; }
    public int ScopedUserRoleId { get; set; }
    public List<int> ProjectIds { get; set; } = new();
    public List<int> EnvironmentIds { get; set; } = new();
    public List<int> ProjectGroupIds { get; set; } = new();
}

public class UpdateRoleScopeResponse : SquidResponse<ScopedUserRoleDto>
{
}
