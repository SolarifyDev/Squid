namespace Squid.Message.Enums;

/// <summary>
/// 变量数据类型枚举
/// </summary>
public enum VariableType
{
    /// <summary>
    /// 字符串类型
    /// </summary>
    String = 1,
    
    /// <summary>
    /// 数字类型
    /// </summary>
    Number = 2,
    
    /// <summary>
    /// 布尔类型
    /// </summary>
    Boolean = 3,
    
    /// <summary>
    /// 密码类型
    /// </summary>
    Password = 4,
    
    /// <summary>
    /// 证书类型
    /// </summary>
    Certificate = 5,
    
    /// <summary>
    /// 多行文本类型
    /// </summary>
    MultiLineText = 6,
    
    /// <summary>
    /// 选择列表类型
    /// </summary>
    SelectList = 7
}
