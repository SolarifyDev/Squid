namespace Squid.Message.Models.Authorization;

public class ScopedUserRoleDto
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    public int UserRoleId { get; set; }
    public string UserRoleName { get; set; }
    public int? SpaceId { get; set; }
    public List<int> ProjectIds { get; set; } = new();
    public List<int> EnvironmentIds { get; set; } = new();
    public List<int> ProjectGroupIds { get; set; } = new();
}
