namespace Squid.Core.Configuration;

/// <summary>
/// 变量加密配置选项
/// </summary>
public class VariableEncryptionOptions
{
    public const string SectionName = "Security:VariableEncryption";

    /// <summary>
    /// 主加密密钥 (Base64编码)
    /// 在生产环境中应该从安全的密钥管理服务获取
    /// </summary>
    public string MasterKey { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用变量加密
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 密钥派生迭代次数
    /// </summary>
    public int KeyDerivationIterations { get; set; } = 10000;

    /// <summary>
    /// 是否在日志中记录加密操作
    /// </summary>
    public bool LogEncryptionOperations { get; set; } = false;

    /// <summary>
    /// 加密算法名称
    /// </summary>
    public string Algorithm { get; set; } = "AES-256-GCM";
}
