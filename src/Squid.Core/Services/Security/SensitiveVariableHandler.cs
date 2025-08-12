using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Security;

/// <summary>
/// 敏感变量处理器，负责在DTO和Domain之间转换时处理敏感数据
/// </summary>
public class SensitiveVariableHandler : IScopedDependency
{
    private const string MASKED_VALUE = "***SENSITIVE***";
    
    /// <summary>
    /// 屏蔽敏感变量的值，用于API响应
    /// </summary>
    /// <param name="variableDto">变量DTO</param>
    /// <param name="maskSensitiveValues">是否屏蔽敏感值</param>
    /// <returns>处理后的变量DTO</returns>
    public VariableDto MaskSensitiveValue(VariableDto variableDto, bool maskSensitiveValues = true)
    {
        if (!maskSensitiveValues || !variableDto.IsSensitive)
            return variableDto;

        return new VariableDto
        {
            Id = variableDto.Id,
            VariableSetId = variableDto.VariableSetId,
            Name = variableDto.Name,
            Value = MASKED_VALUE, // 屏蔽敏感值
            Description = variableDto.Description,
            Type = variableDto.Type,
            IsSensitive = variableDto.IsSensitive,
            SortOrder = variableDto.SortOrder,
            LastModifiedOn = variableDto.LastModifiedOn,
            LastModifiedBy = variableDto.LastModifiedBy,
            Scopes = variableDto.Scopes
        };
    }

    /// <summary>
    /// 批量屏蔽敏感变量的值
    /// </summary>
    /// <param name="variables">变量DTO列表</param>
    /// <param name="maskSensitiveValues">是否屏蔽敏感值</param>
    /// <returns>处理后的变量DTO列表</returns>
    public List<VariableDto> MaskSensitiveValues(List<VariableDto> variables, bool maskSensitiveValues = true)
    {
        return variables.Select(v => MaskSensitiveValue(v, maskSensitiveValues)).ToList();
    }

    /// <summary>
    /// 屏蔽VariableSetDto中的敏感变量值
    /// </summary>
    /// <param name="variableSetDto">变量集DTO</param>
    /// <param name="maskSensitiveValues">是否屏蔽敏感值</param>
    /// <returns>处理后的变量集DTO</returns>
    public VariableSetDto MaskSensitiveValues(VariableSetDto variableSetDto, bool maskSensitiveValues = true)
    {
        if (!maskSensitiveValues)
            return variableSetDto;

        variableSetDto.Variables = MaskSensitiveValues(variableSetDto.Variables, maskSensitiveValues);
        return variableSetDto;
    }

    /// <summary>
    /// 检查值是否被屏蔽
    /// </summary>
    /// <param name="value">值</param>
    /// <returns>是否被屏蔽</returns>
    public bool IsMaskedValue(string value)
    {
        return value == MASKED_VALUE;
    }

    /// <summary>
    /// 处理变量更新时的敏感值逻辑
    /// 如果新值是屏蔽值，则保持原值不变
    /// </summary>
    /// <param name="newVariable">新的变量值</param>
    /// <param name="existingVariable">现有的变量值</param>
    /// <returns>处理后的变量值</returns>
    public string HandleSensitiveValueUpdate(VariableDto newVariable, Variable existingVariable)
    {
        // 如果不是敏感变量，直接返回新值
        if (!newVariable.IsSensitive)
            return newVariable.Value;

        // 如果新值是屏蔽值，保持原值不变
        if (IsMaskedValue(newVariable.Value))
            return existingVariable.Value;

        // 否则返回新值（将被加密）
        return newVariable.Value;
    }
}
