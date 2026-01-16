using Squid.Message.Models.Deployments.Variable;

namespace Squid.Message.Models.Deployments.Snapshots;

public class VariableSetSnapshotDataDto
{
    public List<VariableDto> Variables { get; set; } = new();
}
