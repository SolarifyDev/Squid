namespace Squid.Core.Services.Authorization;

public class ResourceScopeGrant
{
    public int? SpaceId { get; init; }
    public HashSet<int> ProjectIds { get; init; } = new();
    public HashSet<int> EnvironmentIds { get; init; } = new();
    public HashSet<int> ProjectGroupIds { get; init; } = new();
}
