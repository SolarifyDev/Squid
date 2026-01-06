using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

public class VariableSetSnapshotDto
{
    public int Id { get; set; }
    
    public List<VariableDto> Variables { get; set; } = new List<VariableDto>();
    
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string CreatedBy { get; set; }
}
