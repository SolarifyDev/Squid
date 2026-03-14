namespace Squid.Message.Enums;

/// <summary>
/// 变量作用域类型枚举
/// </summary>
public enum VariableScopeType
{
    /// <summary>
    /// 环境作用域
    /// </summary>
    Environment = 1,
    
    /// <summary>
    /// 机器作用域
    /// </summary>
    Machine = 2,

    /// <summary>
    /// 目标角色作用域 (Target Tag)
    /// </summary>
    Role = 3,

    /// <summary>
    /// 频道作用域
    /// </summary>
    Channel = 4,

    /// <summary>
    /// 部署步骤动作作用域
    /// </summary>
    Action = 5,

    /// <summary>
    /// 部署流程作用域
    /// </summary>
    Process = 6
}
