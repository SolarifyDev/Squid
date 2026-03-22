using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace Squid.Tentacle.ScriptExecution;

/// <summary>
/// Extracts sensitive variable values from workspace files for output masking.
/// Decrypts sensitiveVariables.json using AES-128-CBC with the accompanying .key file.
/// </summary>
internal static class SensitiveVariableDecryptor
{
    private const int PasswordSaltIterations = 1000;
    private static readonly byte[] PasswordPaddingSalt = Encoding.UTF8.GetBytes("SquidDep");
    private static readonly byte[] IvPrefix = "IV__"u8.ToArray();

    internal static HashSet<string> ExtractSensitiveValues(string workDir)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var sensitiveFile = Path.Combine(workDir, "sensitiveVariables.json");
            var keyFile = sensitiveFile + ".key";

            if (!File.Exists(sensitiveFile) || !File.Exists(keyFile))
                return result;

            var password = File.ReadAllText(keyFile).Trim();
            var cipherData = File.ReadAllBytes(sensitiveFile);
            var json = Decrypt(cipherData, password);
            var variables = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            if (variables == null)
                return result;

            foreach (var value in variables.Values)
            {
                if (!string.IsNullOrEmpty(value))
                    result.Add(value);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to extract sensitive values for masking from {WorkDir}", workDir);
        }

        return result;
    }

    private static string Decrypt(byte[] cipherData, string password)
    {
        if (cipherData.Length < IvPrefix.Length + 16)
            throw new InvalidOperationException("Invalid encrypted data: too short");

        for (var i = 0; i < IvPrefix.Length; i++)
        {
            if (cipherData[i] != IvPrefix[i])
                throw new InvalidOperationException("Invalid encrypted data: missing IV prefix");
        }

        var iv = cipherData[IvPrefix.Length..(IvPrefix.Length + 16)];
        var encrypted = cipherData[(IvPrefix.Length + 16)..];
        var key = DeriveKey(password);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Key = key;
        aes.IV = iv;

        using var transform = aes.CreateDecryptor();
        using var stream = new MemoryStream(encrypted);
        using var cryptoStream = new CryptoStream(stream, transform, CryptoStreamMode.Read);
        using var reader = new StreamReader(cryptoStream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    private static byte[] DeriveKey(string password)
    {
        using var generator = new Rfc2898DeriveBytes(password, PasswordPaddingSalt, PasswordSaltIterations, HashAlgorithmName.SHA1);
        return generator.GetBytes(16);
    }
}
