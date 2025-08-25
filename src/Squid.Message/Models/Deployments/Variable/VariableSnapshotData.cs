using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

public class VariableSnapshotData
{
    public string Name { get; set; }
    public string Value { get; set; }
    public VariableType Type { get; set; }
    public bool IsSensitive { get; set; }
    public string Description { get; set; }
    public int SortOrder { get; set; }
    public List<VariableScopeData> Scopes { get; set; } = new List<VariableScopeData>();
}
