using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Squid.Core.Services.Security;

/// <summary>
/// 变量加密服务实现
/// 使用AES-256-GCM加密算法，提供认证加密
/// </summary>
public class VariableEncryptionService : IVariableEncryptionService, IScopedDependency
{
    private readonly ILogger<VariableEncryptionService> _logger;
    private readonly IConfiguration _configuration;
    private readonly byte[] _masterKey;

    public VariableEncryptionService(ILogger<VariableEncryptionService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _masterKey = GetOrCreateMasterKey();
    }

    public async Task<string> EncryptAsync(string plainText, int variableSetId)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        try
        {
            var derivedKey = DeriveKey(_masterKey, variableSetId);
            var encryptedData = EncryptWithAesGcm(plainText, derivedKey);
            
            // 添加前缀标识这是加密数据
            var result = $"SQUID_ENCRYPTED:{Convert.ToBase64String(encryptedData)}";
            
            _logger.LogDebug("Successfully encrypted variable for VariableSet {VariableSetId}", variableSetId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt variable for VariableSet {VariableSetId}", variableSetId);
            throw new InvalidOperationException("Failed to encrypt sensitive variable", ex);
        }
    }

    public async Task<string> DecryptAsync(string encryptedText, int variableSetId)
    {
        if (string.IsNullOrEmpty(encryptedText) || !IsValidEncryptedValue(encryptedText))
            return encryptedText;

        try
        {
            // 移除前缀
            var base64Data = encryptedText.Substring("SQUID_ENCRYPTED:".Length);
            var encryptedData = Convert.FromBase64String(base64Data);
            
            var derivedKey = DeriveKey(_masterKey, variableSetId);
            var plainText = DecryptWithAesGcm(encryptedData, derivedKey);
            
            _logger.LogDebug("Successfully decrypted variable for VariableSet {VariableSetId}", variableSetId);
            return plainText;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt variable for VariableSet {VariableSetId}", variableSetId);
            throw new InvalidOperationException("Failed to decrypt sensitive variable", ex);
        }
    }

    public async Task<List<Variable>> EncryptSensitiveVariablesAsync(
        List<Variable> variables, 
        int variableSetId)
    {
        var result = new List<Variable>();
        
        foreach (var variable in variables)
        {
            var encryptedVariable = new Variable
            {
                Id = variable.Id,
                VariableSetId = variable.VariableSetId,
                Name = variable.Name,
                Description = variable.Description,
                Type = variable.Type,
                IsSensitive = variable.IsSensitive,
                SortOrder = variable.SortOrder,
                LastModifiedOn = variable.LastModifiedOn,
                LastModifiedBy = variable.LastModifiedBy,
                Value = variable.IsSensitive 
                    ? await EncryptAsync(variable.Value, variableSetId)
                    : variable.Value
            };
            
            result.Add(encryptedVariable);
        }
        
        return result;
    }

    public async Task<List<Variable>> DecryptSensitiveVariablesAsync(
        List<Variable> variables, 
        int variableSetId)
    {
        var result = new List<Variable>();
        
        foreach (var variable in variables)
        {
            var decryptedVariable = new Variable
            {
                Id = variable.Id,
                VariableSetId = variable.VariableSetId,
                Name = variable.Name,
                Description = variable.Description,
                Type = variable.Type,
                IsSensitive = variable.IsSensitive,
                SortOrder = variable.SortOrder,
                LastModifiedOn = variable.LastModifiedOn,
                LastModifiedBy = variable.LastModifiedBy,
                Value = variable.IsSensitive 
                    ? await DecryptAsync(variable.Value, variableSetId)
                    : variable.Value
            };
            
            result.Add(decryptedVariable);
        }
        
        return result;
    }

    public bool IsValidEncryptedValue(string encryptedText)
    {
        return !string.IsNullOrEmpty(encryptedText) && 
               encryptedText.StartsWith("SQUID_ENCRYPTED:");
    }

    private byte[] GetOrCreateMasterKey()
    {
        // 从配置中获取主密钥，如果不存在则生成一个
        var keyBase64 = _configuration["Security:VariableEncryption:MasterKey"];
        
        if (!string.IsNullOrEmpty(keyBase64))
        {
            try
            {
                return Convert.FromBase64String(keyBase64);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invalid master key format in configuration");
                throw new InvalidOperationException("Invalid master key configuration", ex);
            }
        }
        
        // 在生产环境中，这应该从安全的密钥管理服务获取
        _logger.LogWarning("No master key configured, using default key. This is not secure for production!");
        
        // 生成一个默认密钥（仅用于开发环境）
        using var rng = RandomNumberGenerator.Create();
        var key = new byte[32]; // 256 bits
        rng.GetBytes(key);
        
        _logger.LogInformation("Generated new master key: {MasterKey}", Convert.ToBase64String(key));
        return key;
    }

    private static byte[] DeriveKey(byte[] masterKey, int variableSetId)
    {
        // 使用PBKDF2从主密钥和变量集ID派生特定密钥
        var salt = BitConverter.GetBytes(variableSetId);
        Array.Resize(ref salt, 16); // 确保salt长度为16字节
        
        using var pbkdf2 = new Rfc2898DeriveBytes(masterKey, salt, 10000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32); // 256 bits
    }

    private static byte[] EncryptWithAesGcm(string plainText, byte[] key)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = new byte[12]; // 96 bits nonce for GCM
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[16]; // 128 bits authentication tag
        
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(nonce);
        
        using var aes = new AesGcm(key);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);
        
        // 组合 nonce + tag + ciphertext
        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);
        
        return result;
    }

    private static string DecryptWithAesGcm(byte[] encryptedData, byte[] key)
    {
        if (encryptedData.Length < 28) // 12 (nonce) + 16 (tag) = 28 minimum
            throw new ArgumentException("Invalid encrypted data length");
        
        var nonce = new byte[12];
        var tag = new byte[16];
        var ciphertext = new byte[encryptedData.Length - 28];
        
        Buffer.BlockCopy(encryptedData, 0, nonce, 0, 12);
        Buffer.BlockCopy(encryptedData, 12, tag, 0, 16);
        Buffer.BlockCopy(encryptedData, 28, ciphertext, 0, ciphertext.Length);
        
        var plainBytes = new byte[ciphertext.Length];
        
        using var aes = new AesGcm(key);
        aes.Decrypt(nonce, ciphertext, tag, plainBytes);
        
        return Encoding.UTF8.GetString(plainBytes);
    }
}
