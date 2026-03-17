using Squid.Message.Enums;

namespace Squid.Message.Models.Authorization;

public class ResourceScopeRequest
{
    public int UserId { get; set; }
    public Permission Permission { get; set; }
    public int? SpaceId { get; set; }
}
