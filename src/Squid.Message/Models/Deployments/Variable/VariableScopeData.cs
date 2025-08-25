using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

public class VariableScopeData
{
    public VariableScopeType ScopeType { get; set; }
    public string ScopeValue { get; set; }
}
