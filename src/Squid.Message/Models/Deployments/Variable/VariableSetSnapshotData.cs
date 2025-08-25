using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

public class VariableSetSnapshotData
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public VariableSetOwnerType OwnerType { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<VariableSnapshotData> Variables { get; set; } = new List<VariableSnapshotData>();

    public Dictionary<string, List<string>> ScopeDefinitions { get; set; } = new Dictionary<string, List<string>>();
}
