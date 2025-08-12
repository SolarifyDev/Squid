namespace Squid.Core.Services.Security;

/// <summary>
/// 变量加密服务接口
/// </summary>
public interface IVariableEncryptionService
{
    /// <summary>
    /// 加密敏感变量值
    /// </summary>
    /// <param name="plainText">明文值</param>
    /// <param name="variableSetId">变量集ID，用于生成特定的加密密钥</param>
    /// <returns>加密后的值</returns>
    Task<string> EncryptAsync(string plainText, int variableSetId);

    /// <summary>
    /// 解密敏感变量值
    /// </summary>
    /// <param name="encryptedText">加密的值</param>
    /// <param name="variableSetId">变量集ID，用于生成特定的解密密钥</param>
    /// <returns>解密后的明文值</returns>
    Task<string> DecryptAsync(string encryptedText, int variableSetId);

    /// <summary>
    /// 批量加密变量值
    /// </summary>
    /// <param name="variables">变量列表</param>
    /// <param name="variableSetId">变量集ID</param>
    /// <returns>加密后的变量列表</returns>
    Task<List<Variable>> EncryptSensitiveVariablesAsync(
        List<Variable> variables, 
        int variableSetId);

    /// <summary>
    /// 批量解密变量值
    /// </summary>
    /// <param name="variables">变量列表</param>
    /// <param name="variableSetId">变量集ID</param>
    /// <returns>解密后的变量列表</returns>
    Task<List<Variable>> DecryptSensitiveVariablesAsync(
        List<Variable> variables, 
        int variableSetId);

    /// <summary>
    /// 验证加密值的完整性
    /// </summary>
    /// <param name="encryptedText">加密的值</param>
    /// <returns>是否有效</returns>
    bool IsValidEncryptedValue(string encryptedText);
}
