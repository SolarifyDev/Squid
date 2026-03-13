using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class ScriptExecutionHelperTests
{
    [Fact]
    public void CreateVariableFileContents_NullVariables_ReturnsEmptyJsonAndNoPassword()
    {
        var (variableJson, sensitiveJson, password) = ScriptExecutionHelper.CreateVariableFileContents(null);

        Encoding.UTF8.GetString(variableJson).ShouldBe("{}");
        Encoding.UTF8.GetString(sensitiveJson).ShouldBe("{}");
        password.ShouldBeEmpty();
    }

    [Fact]
    public void CreateVariableFileContents_EmptyVariables_ReturnsEmptyJsonAndNoPassword()
    {
        var (variableJson, sensitiveJson, password) = ScriptExecutionHelper.CreateVariableFileContents(new List<VariableDto>());

        Encoding.UTF8.GetString(variableJson).ShouldBe("{}");
        Encoding.UTF8.GetString(sensitiveJson).ShouldBe("{}");
        password.ShouldBeEmpty();
    }

    [Fact]
    public void CreateVariableFileContents_NonSensitiveOnly_CorrectJsonAndEmptyPassword()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "Env", Value = "Production", IsSensitive = false },
            new() { Name = "Port", Value = "8080", IsSensitive = false }
        };

        var (variableJson, sensitiveJson, password) = ScriptExecutionHelper.CreateVariableFileContents(variables);

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(variableJson);
        dict.ShouldContainKeyAndValue("Env", "Production");
        dict.ShouldContainKeyAndValue("Port", "8080");
        Encoding.UTF8.GetString(sensitiveJson).ShouldBe("{}");
        password.ShouldBeEmpty();
    }

    [Fact]
    public void CreateVariableFileContents_SensitiveOnly_EncryptedBytesAndNonEmptyPassword()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "Token", Value = "secret-value", IsSensitive = true }
        };

        var (variableJson, sensitiveJson, password) = ScriptExecutionHelper.CreateVariableFileContents(variables);

        Encoding.UTF8.GetString(variableJson).ShouldBe("{}");
        password.ShouldNotBeNullOrEmpty();
        sensitiveJson.Length.ShouldBeGreaterThan(0);

        // Decrypt and verify
        var encryption = new SquidVariableEncryption(password);
        var decrypted = DecryptSensitive(sensitiveJson, password);
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(decrypted);
        dict.ShouldContainKeyAndValue("Token", "secret-value");
    }

    [Fact]
    public void CreateVariableFileContents_MixedVariables_SeparatesCorrectly()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "Env", Value = "Production", IsSensitive = false },
            new() { Name = "Token", Value = "secret", IsSensitive = true },
            new() { Name = "Host", Value = "localhost", IsSensitive = false }
        };

        var (variableJson, sensitiveJson, password) = ScriptExecutionHelper.CreateVariableFileContents(variables);

        var nonSensitive = JsonSerializer.Deserialize<Dictionary<string, string>>(variableJson);
        nonSensitive.ShouldContainKey("Env");
        nonSensitive.ShouldContainKey("Host");
        nonSensitive.ShouldNotContainKey("Token");

        password.ShouldNotBeNullOrEmpty();
        var sensitive = JsonSerializer.Deserialize<Dictionary<string, string>>(DecryptSensitive(sensitiveJson, password));
        sensitive.ShouldContainKeyAndValue("Token", "secret");
    }

    [Fact]
    public void CreateVariableFileContents_NullNameVariable_IsSkipped()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = null, Value = "should-skip", IsSensitive = false },
            new() { Name = "Valid", Value = "kept", IsSensitive = false }
        };

        var (variableJson, _, _) = ScriptExecutionHelper.CreateVariableFileContents(variables);

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(variableJson);
        dict.Count.ShouldBe(1);
        dict.ShouldContainKeyAndValue("Valid", "kept");
    }

    [Fact]
    public void CreateVariableFileContents_DuplicateNames_LastWins()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "Key", Value = "first", IsSensitive = false },
            new() { Name = "Key", Value = "second", IsSensitive = false }
        };

        var (variableJson, _, _) = ScriptExecutionHelper.CreateVariableFileContents(variables);

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(variableJson);
        dict["Key"].ShouldBe("second");
    }

    [Fact]
    public void CreateVariableFileContents_NullValue_TreatedAsEmpty()
    {
        var variables = new List<VariableDto>
        {
            new() { Name = "NullVal", Value = null, IsSensitive = false }
        };

        var (variableJson, _, _) = ScriptExecutionHelper.CreateVariableFileContents(variables);

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(variableJson);
        dict.ShouldContainKeyAndValue("NullVal", "");
    }

    private static string DecryptSensitive(byte[] encrypted, string password)
    {
        // Read IV from prefix: "IV__" (4 bytes) + 16 bytes IV
        const int prefixLen = 4;
        const int ivLen = 16;
        var iv = new byte[ivLen];
        Array.Copy(encrypted, prefixLen, iv, 0, ivLen);

        var ciphertext = new byte[encrypted.Length - prefixLen - ivLen];
        Array.Copy(encrypted, prefixLen + ivLen, ciphertext, 0, ciphertext.Length);

        var key = SquidVariableEncryption.GetEncryptionKey(password);

        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
