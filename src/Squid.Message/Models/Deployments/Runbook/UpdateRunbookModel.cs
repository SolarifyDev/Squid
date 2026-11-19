namespace Squid.Message.Models.Deployments.Runbook;

public class UpdateRunbookModel
{
    public string Name { get; set; }

    public string Description { get; set; }

    public List<string> Tags { get; set; } = new();
}
