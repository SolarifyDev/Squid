using Squid.Message.Domain;
using Squid.Message.Enums;

namespace Squid.Core.Persistence.Data.Domain.Deployments;

public class Variable : IEntity<int>
{
    public int Id { get; set; }

    public int VariableSetId { get; set; }

    public string Name { get; set; }

    public string Value { get; set; }

    public string Description { get; set; }

    public VariableType Type { get; set; } = VariableType.String;

    public bool IsSensitive { get; set; } = false;

    public int SortOrder { get; set; } = 0;

    public DateTimeOffset? LastModifiedOn { get; set; }

    public string LastModifiedBy { get; set; }
}
