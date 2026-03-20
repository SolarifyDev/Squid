namespace Squid.Message.Models.Spaces;

public class SpaceDto
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Slug { get; set; }
    public string Description { get; set; }
    public bool IsDefault { get; set; }
    public bool TaskQueueStopped { get; set; }
    public bool IsPrivate { get; set; }
}
