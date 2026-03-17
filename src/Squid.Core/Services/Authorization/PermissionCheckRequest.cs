using Squid.Message.Enums;

namespace Squid.Core.Services.Authorization;

public class PermissionCheckRequest
{
    public int UserId { get; set; }
    public Permission Permission { get; set; }
    public int? SpaceId { get; set; }
    public int? ProjectId { get; set; }
    public int? EnvironmentId { get; set; }
    public int? ProjectGroupId { get; set; }
}
