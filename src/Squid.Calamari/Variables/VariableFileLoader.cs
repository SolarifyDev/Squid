using System.Text.Json;

namespace Squid.Calamari.Variables;

public static class VariableFileLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Loads non-sensitive variables from a JSON file produced by ScriptExecutionHelper.
    /// Format: {"VariableName": "Value", ...}
    /// </summary>
    public static Dictionary<string, string> Load(string variablesPath)
    {
        if (!File.Exists(variablesPath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var json = File.ReadAllText(variablesPath);

        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decrypts and loads sensitive variables from an AES-encrypted JSON file.
    /// Format after decryption: {"VariableName": "Value", ...}
    /// </summary>
    public static Dictionary<string, string> LoadSensitive(string sensitivePath, string password)
    {
        if (!File.Exists(sensitivePath) || string.IsNullOrEmpty(password))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var encryptedBytes = File.ReadAllBytes(sensitivePath);

        if (encryptedBytes.Length <= 4)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var decryptor = new SensitiveVariableDecryptor(password);
        var json = decryptor.Decrypt(encryptedBytes);

        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static Dictionary<string, string> MergeAll(
        string variablesPath, string? sensitivePath, string? password)
    {
        var merged = Load(variablesPath);

        if (!string.IsNullOrEmpty(sensitivePath) && !string.IsNullOrEmpty(password))
        {
            foreach (var kv in LoadSensitive(sensitivePath, password))
                merged[kv.Key] = kv.Value;
        }

        return merged;
    }
}
