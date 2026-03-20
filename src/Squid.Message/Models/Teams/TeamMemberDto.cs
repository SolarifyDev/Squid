namespace Squid.Message.Models.Teams;

public class TeamMemberDto
{
    public int TeamId { get; set; }
    public int UserId { get; set; }
    public string UserName { get; set; }
    public string DisplayName { get; set; }
}
