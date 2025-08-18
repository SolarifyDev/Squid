using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Security;

public class SensitiveVariableHandler : IScopedDependency
{
    private const string MASKED_VALUE = "***SENSITIVE***";
    
    public VariableDto MaskSensitiveValue(VariableDto variableDto, bool maskSensitiveValues = true)
    {
        if (!maskSensitiveValues || !variableDto.IsSensitive)
            return variableDto;

        return new VariableDto
        {
            Id = variableDto.Id,
            VariableSetId = variableDto.VariableSetId,
            Name = variableDto.Name,
            Value = MASKED_VALUE,
            Description = variableDto.Description,
            Type = variableDto.Type,
            IsSensitive = variableDto.IsSensitive,
            SortOrder = variableDto.SortOrder,
            LastModifiedOn = variableDto.LastModifiedOn,
            LastModifiedBy = variableDto.LastModifiedBy,
            Scopes = variableDto.Scopes
        };
    }

    public List<VariableDto> MaskSensitiveValues(List<VariableDto> variables, bool maskSensitiveValues = true)
    {
        return variables.Select(v => MaskSensitiveValue(v, maskSensitiveValues)).ToList();
    }

    public VariableSetDto MaskSensitiveValues(VariableSetDto variableSetDto, bool maskSensitiveValues = true)
    {
        if (!maskSensitiveValues)
            return variableSetDto;

        variableSetDto.Variables = MaskSensitiveValues(variableSetDto.Variables, maskSensitiveValues);
        return variableSetDto;
    }

    public bool IsMaskedValue(string value)
    {
        return value == MASKED_VALUE;
    }

    public string HandleSensitiveValueUpdate(VariableDto newVariable, Variable existingVariable)
    {
        if (!newVariable.IsSensitive)
            return newVariable.Value;
        
        if (IsMaskedValue(newVariable.Value))
            return existingVariable.Value;

        return newVariable.Value;
    }
}
