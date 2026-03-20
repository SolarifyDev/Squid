using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

public class VariableModel
{
    public string Name { get; set; }
    public string Value { get; set; }
    public string Description { get; set; }
    public VariableType Type { get; set; }
    public bool IsSensitive { get; set; }
    public int SortOrder { get; set; }
    public string PromptLabel { get; set; }
    public string PromptDescription { get; set; }
    public bool PromptRequired { get; set; }
    public List<VariableScopeModel> Scopes { get; set; } = new();
}
