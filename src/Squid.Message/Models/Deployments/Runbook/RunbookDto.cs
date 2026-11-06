namespace Squid.Message.Models.Deployments.Runbook;

public class RunbookDto
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int SpaceId { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public List<string> Tags { get; set; } = new();

    public int ProcessId { get; set; }

    public int VariableSetId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    public int CreatedBy { get; set; }

    public DateTimeOffset LastModifiedDate { get; set; }

    public int LastModifiedBy { get; set; }
}
