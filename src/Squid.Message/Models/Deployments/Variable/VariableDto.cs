using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

public class VariableDto
{
    public int Id { get; set; }

    public int VariableSetId { get; set; }

    public string Name { get; set; }

    public string Value { get; set; }

    public string Description { get; set; }

    public VariableType Type { get; set; }

    public bool IsSensitive { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset? LastModifiedOn { get; set; }

    public string LastModifiedBy { get; set; }

    public List<VariableScopeDto> Scopes { get; set; } = new List<VariableScopeDto>();
}
