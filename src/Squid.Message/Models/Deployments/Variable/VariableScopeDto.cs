using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

public class VariableScopeDto
{
    public int Id { get; set; }

    public int VariableId { get; set; }

    public VariableScopeType ScopeType { get; set; }

    public string ScopeValue { get; set; }
}
