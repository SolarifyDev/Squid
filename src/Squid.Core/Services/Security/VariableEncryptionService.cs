using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Squid.Core.Settings.Security;

namespace Squid.Core.Services.Security;

public class VariableEncryptionService : IVariableEncryptionService
{
    private readonly byte[] _masterKey;
    private readonly SecuritySetting _securitySetting;
    private readonly string _encryptionPrefix = "SQUID_ENCRYPTED:";

    public VariableEncryptionService(SecuritySetting securitySetting)
    {
        _securitySetting = securitySetting;
        _masterKey = GetOrCreateMasterKey();
    }

    public string EncryptAsync(string plainText, int variableSetId)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        try
        {
            var derivedKey = DeriveKey(_masterKey, variableSetId);
            var encryptedData = EncryptWithAesGcm(plainText, derivedKey);
            
            var result = $"{_encryptionPrefix}{Convert.ToBase64String(encryptedData)}";
            
            Log.Information("Successfully encrypted variable for VariableSet {VariableSetId}", variableSetId);
            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to encrypt variable for VariableSet {variableSetId}", ex);
        }
    }

    public async Task<string> DecryptAsync(string encryptedText, int variableSetId)
    {
        if (string.IsNullOrEmpty(encryptedText) || !IsValidEncryptedValue(encryptedText))
            return encryptedText;

        try
        {
            var base64Data = encryptedText.Substring(_encryptionPrefix.Length);
            var encryptedData = Convert.FromBase64String(base64Data);
            
            var derivedKey = DeriveKey(_masterKey, variableSetId);
            var plainText = DecryptWithAesGcm(encryptedData, derivedKey);
            
            Log.Information("Successfully decrypted variable for VariableSet {VariableSetId}", variableSetId);
            return plainText;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to decrypt variable for VariableSet {variableSetId}", ex);
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
                    ? EncryptAsync(variable.Value, variableSetId)
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
        return !string.IsNullOrEmpty(encryptedText) && encryptedText.StartsWith(_encryptionPrefix);
    }

    private byte[] GetOrCreateMasterKey()
    {
        var keyBase64 = _securitySetting.VariableEncryption.MasterKey;
        
        try
        {
            return Convert.FromBase64String(keyBase64);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Invalid master key configuration", ex);
        }
    }

    private static byte[] DeriveKey(byte[] masterKey, int variableSetId)
    {
        var salt = BitConverter.GetBytes(variableSetId);
        Array.Resize(ref salt, 16);
        
        using var pbkdf2 = new Rfc2898DeriveBytes(masterKey, salt, 10000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    private static byte[] EncryptWithAesGcm(string plainText, byte[] key)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = new byte[12];
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[16];
        
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(nonce);
        
        using var aes = new AesGcm(key);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);
        

        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);
        
        return result;
    }

    private static string DecryptWithAesGcm(byte[] encryptedData, byte[] key)
    {
        if (encryptedData.Length < 28)
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
