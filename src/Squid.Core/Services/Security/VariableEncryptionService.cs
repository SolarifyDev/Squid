using System.Security.Cryptography;
using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Settings.Security;
using Squid.Message.Hardening;

namespace Squid.Core.Services.Security;

public class VariableEncryptionService : IVariableEncryptionService
{
    /// <summary>
    /// Env var that selects the enforcement mode for MasterKey validation.
    /// Follows the project-wide three-mode hardening pattern (CLAUDE.md
    /// §"Hardening Three-Mode Enforcement").
    ///
    /// <para>Recognised values: <c>off</c> / <c>warn</c> / <c>strict</c>.
    /// Default (unset / blank) is <see cref="EnforcementMode.Warn"/> — preserves
    /// backward compat for deploys that haven't set a real master key yet,
    /// while logging structured warnings at startup so the insecure config is
    /// visible in Seq.</para>
    ///
    /// <para>Pinned literal — renaming breaks every operator who set the env
    /// var by its documented name. See
    /// <c>VariableEncryptionServiceMasterKeyTests.EnforcementEnvVar_ConstantNamePinned</c>.</para>
    /// </summary>
    public const string EnforcementEnvVar = "SQUID_MASTER_KEY_ENFORCEMENT";

    private const int RequiredKeyLengthBytes = 32;

    private readonly byte[] _masterKey;
    private readonly SecuritySetting _securitySetting;
    private readonly string _encryptionPrefix = "SQUID_ENCRYPTED:";

    public VariableEncryptionService(SecuritySetting securitySetting)
    {
        _securitySetting = securitySetting;
        _masterKey = ValidateMasterKey(_securitySetting.MasterKey, ReadEnforcementMode());
    }

    /// <summary>
    /// Validates a base64-encoded master key and returns the decoded bytes.
    /// Called from the constructor and exposed <c>internal static</c> so the
    /// unit suite can exercise every (input × mode) cell without a full DI
    /// container.
    ///
    /// <para><b>Behaviour matrix</b>:</para>
    /// <list type="table">
    ///   <item><term>null / empty / whitespace</term>
    ///         <description>Off → return <c>byte[0]</c>; Warn → <c>byte[0]</c> +
    ///         warn; Strict → throw.</description></item>
    ///   <item><term>not valid base64</term>
    ///         <description>ALWAYS throw. No mode can save you — broken format
    ///         means no decoded key bytes exist for the crypto path.</description></item>
    ///   <item><term>decoded &lt; 32 bytes</term>
    ///         <description>Off → return decoded; Warn → decoded + warn;
    ///         Strict → throw.</description></item>
    ///   <item><term>all-zero bytes</term>
    ///         <description>Off → return decoded; Warn → decoded + warn;
    ///         Strict → throw.</description></item>
    ///   <item><term>valid (32+ random non-zero bytes)</term>
    ///         <description>All modes return decoded bytes silently.</description></item>
    /// </list>
    /// </summary>
    internal static byte[] ValidateMasterKey(string? rawBase64, EnforcementMode mode)
    {
        const string settingPath = "Security:VariableEncryption:MasterKey";

        if (string.IsNullOrWhiteSpace(rawBase64))
            return EnforceEmpty(mode, settingPath);

        var decoded = TryDecodeBase64OrThrow(rawBase64, settingPath);

        if (decoded.Length < RequiredKeyLengthBytes)
            return EnforceTooShort(mode, decoded, settingPath);

        if (IsAllZero(decoded))
            return EnforceAllZero(mode, decoded, settingPath);

        return decoded;
    }

    private static byte[] EnforceEmpty(EnforcementMode mode, string settingPath)
    {
        switch (mode)
        {
            case EnforcementMode.Off:
                return Array.Empty<byte>();

            case EnforcementMode.Warn:
                Log.Warning(
                    "MasterKey is empty (config path {SettingPath}). Proceeding with a 0-byte key — " +
                    "every encrypted variable is recoverable from a DB dump without the key. " +
                    "Backward-compat mode; set {EnvVar}=strict to refuse start, or fix MasterKey to " +
                    "a base64-encoded 32-byte random value (`openssl rand -base64 32`).",
                    settingPath, EnforcementEnvVar);
                return Array.Empty<byte>();

            case EnforcementMode.Strict:
                throw new InvalidOperationException(
                    $"MasterKey is empty or missing. Set {settingPath} in appsettings.json to a " +
                    "base64-encoded 32-byte random value (e.g. `openssl rand -base64 32`). " +
                    $"To suppress this rejection, set {EnforcementEnvVar}=warn (allow + log warning) " +
                    $"or {EnforcementEnvVar}=off (silent).");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
    }

    private static byte[] TryDecodeBase64OrThrow(string rawBase64, string settingPath)
    {
        try
        {
            return Convert.FromBase64String(rawBase64);
        }
        catch (FormatException ex)
        {
            // Always throw on malformed input — no enforcement mode can recover
            // because there are no decoded key bytes to use. Mode only matters
            // when there's a "valid-but-insecure" choice between accept and reject.
            throw new InvalidOperationException(
                $"MasterKey at {settingPath} is not valid base64 — it could not be decoded. Use a " +
                "base64-encoded 32-byte value (e.g. `openssl rand -base64 32`). This rejection is " +
                "unconditional regardless of the enforcement mode.",
                ex);
        }
    }

    private static byte[] EnforceTooShort(EnforcementMode mode, byte[] decoded, string settingPath)
    {
        switch (mode)
        {
            case EnforcementMode.Off:
                return decoded;

            case EnforcementMode.Warn:
                Log.Warning(
                    "MasterKey at {SettingPath} decodes to {Actual} bytes (recommended ≥ {Required}). " +
                    "Proceeding with the short key — KDF still derives a 32-byte working key but the " +
                    "input entropy is reduced. Backward-compat mode; set {EnvVar}=strict to refuse " +
                    "start, or regenerate with `openssl rand -base64 32`.",
                    settingPath, decoded.Length, RequiredKeyLengthBytes, EnforcementEnvVar);
                return decoded;

            case EnforcementMode.Strict:
                throw new InvalidOperationException(
                    $"MasterKey at {settingPath} decodes to {decoded.Length} bytes; at least " +
                    $"{RequiredKeyLengthBytes} bytes are required for AES-256 key derivation. " +
                    $"Regenerate with `openssl rand -base64 32`. To suppress this rejection, set " +
                    $"{EnforcementEnvVar}=warn (allow + log) or {EnforcementEnvVar}=off (silent).");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
    }

    private static byte[] EnforceAllZero(EnforcementMode mode, byte[] decoded, string settingPath)
    {
        switch (mode)
        {
            case EnforcementMode.Off:
                return decoded;

            case EnforcementMode.Warn:
                Log.Warning(
                    "MasterKey at {SettingPath} is all-zero bytes — recognisable to an attacker without " +
                    "needing the key, making DB-dump offline recovery trivial. Backward-compat mode " +
                    "(typically the committed appsettings default). Set {EnvVar}=strict to refuse start, " +
                    "or regenerate with `openssl rand -base64 32`.",
                    settingPath, EnforcementEnvVar);
                return decoded;

            case EnforcementMode.Strict:
                throw new InvalidOperationException(
                    $"MasterKey at {settingPath} is all-zero bytes — identifiable by any attacker " +
                    "without knowing the key. Regenerate with `openssl rand -base64 32` and set " +
                    $"{settingPath} in your deployment config. To suppress this rejection, set " +
                    $"{EnforcementEnvVar}=warn (allow + log) or {EnforcementEnvVar}=off (silent).");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
    }

    private static bool IsAllZero(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] != 0) return false;

        return true;
    }

    private static EnforcementMode ReadEnforcementMode()
        => EnforcementModeReader.Read(EnforcementEnvVar);

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
