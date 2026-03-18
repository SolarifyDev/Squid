namespace Squid.Message.Models.Spaces;

public class SpaceManagersDto
{
    public List<SpaceManagerTeamDto> Teams { get; set; } = new();
    public List<SpaceManagerUserDto> Users { get; set; } = new();
}

public class SpaceManagerUserDto
{
    public int UserId { get; set; }
    public string UserName { get; set; }
    public string DisplayName { get; set; }
}
