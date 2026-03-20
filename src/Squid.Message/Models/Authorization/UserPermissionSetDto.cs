namespace Squid.Message.Models.Authorization;

public class UserPermissionSetDto
{
    public int UserId { get; set; }
    public List<string> Permissions { get; set; } = new();
}
