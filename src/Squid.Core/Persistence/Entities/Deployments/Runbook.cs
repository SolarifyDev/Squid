namespace Squid.Core.Persistence.Entities.Deployments;

public class Runbook : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int SpaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; }

    public string TagsJson { get; set; } = "[]";

    public int ProcessId { get; set; }

    public int VariableSetId { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
