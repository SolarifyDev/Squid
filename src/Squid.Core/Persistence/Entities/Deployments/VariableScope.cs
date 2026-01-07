using Squid.Message.Enums;

namespace Squid.Core.Persistence.Entities.Deployments;

public class VariableScope : IEntity<int>
{
    public int Id { get; set; }

    public int VariableId { get; set; }

    public VariableScopeType ScopeType { get; set; }

    public string ScopeValue { get; set; }
}
