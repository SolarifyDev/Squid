using Squid.Message.Enums;

namespace Squid.Core.Persistence.Entities.Deployments;

public class Variable : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public int VariableSetId { get; set; }

    public string Name { get; set; }

    public string Value { get; set; }

    public string Description { get; set; }

    public VariableType Type { get; set; } = VariableType.String;

    public bool IsSensitive { get; set; } = false;

    public int SortOrder { get; set; } = 0;

    // Prompt
    public string PromptLabel { get; set; }
    public string PromptDescription { get; set; }
    public bool PromptRequired { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
