namespace Squid.Message.Models.Deployments.Runbook;

public class CreateRunbookModel
{
    public int ProjectId { get; set; }

    public int SpaceId { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public List<string> Tags { get; set; } = new();
}
