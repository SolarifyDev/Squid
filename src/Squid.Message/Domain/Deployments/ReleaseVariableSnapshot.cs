using Squid.Message.Enums;

namespace Squid.Message.Domain.Deployments;

public class ReleaseVariableSnapshot : IEntity<int>
{
    public int Id { get; set; }

    public int ReleaseId { get; set; }

    public int VariableSetId { get; set; }

    public int SnapshotId { get; set; }

    public ReleaseVariableSetType VariableSetType { get; set; }
}
