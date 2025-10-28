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
    /// 角色作用域
    /// </summary>
    Role = 3,
    
    /// <summary>
    /// 渠道作用域
    /// </summary>
    Channel = 4,
    
    /// <summary>
    /// 步骤作用域
    /// </summary>
    Step = 5,
    
    /// <summary>
    /// 进程作用域
    /// </summary>
    Process = 6
}
