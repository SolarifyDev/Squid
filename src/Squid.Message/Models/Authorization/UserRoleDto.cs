namespace Squid.Message.Models.Authorization;

public class UserRoleDto
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public bool IsBuiltIn { get; set; }
    public List<string> Permissions { get; set; } = new();
    public bool CanApplyAtSpaceLevel { get; set; }
    public bool CanApplyAtSystemLevel { get; set; }
}
