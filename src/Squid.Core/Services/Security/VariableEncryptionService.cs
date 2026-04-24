using System.Security.Cryptography;
using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Settings.Security;

namespace Squid.Core.Services.Security;

public class VariableEncryptionService : IVariableEncryptionService
{
    /// <summary>
    /// Env-var escape hatch for dev / CI scenarios where the operator
    /// knowingly uses a weak master key (tests, local dev containers,
    /// ephemeral pipelines). When set to <c>1</c> / <c>true</c> /
    /// <c>yes</c> (case-insensitive), constructor accepts an empty /
    /// too-short / all-zero MasterKey instead of throwing.
    ///
    /// <para>Default behaviour (env var unset) is fail-closed: service
    /// refuses to start unless MasterKey is base64-decodable to ≥ 32
    /// non-zero bytes. Prevents the P0 bug where <c>MasterKey=""</c>
    /// silently yielded a 0-byte key and deterministic per-variable
    /// encryption.</para>
    ///
    /// <para>Pinned literal — renaming breaks dev environments that set
    /// the env var by its documented name.</para>
    /// </summary>
    public const string AllowInsecureMasterKeyEnvVar = "SQUID_ALLOW_INSECURE_MASTER_KEY";

    private const int RequiredKeyLengthBytes = 32;

    private readonly byte[] _masterKey;
    private readonly SecuritySetting _securitySetting;
    private readonly string _encryptionPrefix = "SQUID_ENCRYPTED:";

    public VariableEncryptionService(SecuritySetting securitySetting)
    {
        _securitySetting = securitySetting;
        _masterKey = ValidateMasterKey(_securitySetting.MasterKey, ReadAllowInsecure());
    }

    /// <summary>
    /// Validates a base64-encoded master key and returns the decoded
    /// bytes. Called from the constructor and exposed <c>internal static</c>
    /// so the unit suite can exercise every failure mode without a
    /// full DI container. Throws <see cref="InvalidOperationException"/>
    /// with an actionable message for each rejection case.
    /// <list type="bullet">
    ///   <item>null / empty / whitespace: "set Security:VariableEncryption:MasterKey"</item>
    ///   <item>not valid base64: "MasterKey is not valid base64"</item>
    ///   <item>decoded length &lt; 32 bytes: "MasterKey must be at least 32 bytes"</item>
    ///   <item>all zero bytes: "MasterKey is all-zero — use openssl rand -base64 32"</item>
    /// </list>
    /// The <paramref name="allowInsecure"/> flag bypasses all entropy /
    /// length checks (but NOT the base64-format check — a malformed key
    /// can never produce working crypto).
    /// </summary>
    internal static byte[] ValidateMasterKey(string? rawBase64, bool allowInsecure)
    {
        const string settingPath = "Security:VariableEncryption:MasterKey";

        if (string.IsNullOrWhiteSpace(rawBase64))
        {
            if (allowInsecure)
            {
                Log.Warning(
                    "MasterKey is empty but {EnvVar}=1 is set — proceeding with a 0-byte key. " +
                    "This is catastrophically insecure; dev/CI use only.",
                    AllowInsecureMasterKeyEnvVar);
                return Array.Empty<byte>();
            }

            throw new InvalidOperationException(
                $"MasterKey is empty or missing. Set {settingPath} in appsettings.json " +
                "to a base64-encoded 32-byte random value (e.g. `openssl rand -base64 32`). " +
                $"For dev / CI, set {AllowInsecureMasterKeyEnvVar}=1 to bypass this check.");
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(rawBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"MasterKey is not valid base64. Set {settingPath} to a base64-encoded 32-byte " +
                "value (e.g. `openssl rand -base64 32`). Current value could not be decoded.",
                ex);
        }

        if (decoded.Length < RequiredKeyLengthBytes)
        {
            if (allowInsecure)
            {
                Log.Warning(
                    "MasterKey is {Actual} bytes (need ≥ {Required}) but {EnvVar}=1 is set — " +
                    "proceeding with the undersized key. Dev/CI use only.",
                    decoded.Length, RequiredKeyLengthBytes, AllowInsecureMasterKeyEnvVar);
                return decoded;
            }

            throw new InvalidOperationException(
                $"MasterKey decodes to {decoded.Length} bytes; at least {RequiredKeyLengthBytes} bytes " +
                $"are required for AES-256 key derivation. Regenerate with `openssl rand -base64 32`. " +
                $"For dev / CI, set {AllowInsecureMasterKeyEnvVar}=1 to bypass this check.");
        }

        if (IsAllZero(decoded))
        {
            if (allowInsecure)
            {
                Log.Warning(
                    "MasterKey is all-zero bytes but {EnvVar}=1 is set — proceeding with a " +
                    "predictable key. Dev/CI use only.",
                    AllowInsecureMasterKeyEnvVar);
                return decoded;
            }

            throw new InvalidOperationException(
                $"MasterKey is all-zero bytes — this is the committed appsettings.json default and " +
                "is identifiable by any attacker without knowing the key. Regenerate with " +
                $"`openssl rand -base64 32` and set {settingPath} in your deployment config. " +
                $"For dev / CI where all-zero keys are acceptable, set {AllowInsecureMasterKeyEnvVar}=1.");
        }

        return decoded;
    }

    private static bool IsAllZero(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] != 0) return false;

        return true;
    }

    private static bool ReadAllowInsecure()
    {
        // Fully-qualified System.Environment — the Squid.Core namespace
        // imports an `Environment` entity type (deployment target), and
        // the unqualified reference is ambiguous in this file.
        var raw = System.Environment.GetEnvironmentVariable(AllowInsecureMasterKeyEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return false;

        var normalized = raw.Trim().ToLowerInvariant();

        return normalized.Equals("1", StringComparison.Ordinal)
            || normalized.Equals("true", StringComparison.Ordinal)
            || normalized.Equals("yes", StringComparison.Ordinal);
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
                LastModifiedDate = variable.LastModifiedDate,
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
                LastModifiedDate = variable.LastModifiedDate,
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
