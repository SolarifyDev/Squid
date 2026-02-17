using Squid.Core.VariableSubstitution;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public static class VariableDictionaryFactory
{
    public static VariableDictionary Create(List<VariableDto> variables)
    {
        var dict = new VariableDictionary();

        if (variables == null)
            return dict;

        foreach (var v in variables)
        {
            if (string.IsNullOrEmpty(v?.Name))
                continue;

            dict.Set(v.Name, v.Value ?? "");
        }

        return dict;
    }
}
