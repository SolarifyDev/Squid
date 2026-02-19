using System.Text;
using System.Text.Json;
using Squid.Core.Services.Common;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public static class ScriptExecutionHelper
{
    public static (byte[] VariableJson, byte[] SensitiveVariableJson, string Password) CreateVariableFileContents(
        List<VariableDto> variables)
    {
        if (variables == null || variables.Count == 0)
        {
            var emptyBytes = Encoding.UTF8.GetBytes("{}");
            return (emptyBytes, emptyBytes, string.Empty);
        }

        var nonSensitiveVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sensitiveVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var variable in variables)
        {
            if (variable == null || string.IsNullOrWhiteSpace(variable.Name))
                continue;

            var value = variable.Value ?? string.Empty;

            if (variable.IsSensitive)
                sensitiveVariables[variable.Name] = value;
            else
                nonSensitiveVariables[variable.Name] = value;
        }

        var variableJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(nonSensitiveVariables));

        var password = string.Empty;
        byte[] sensitiveBytes;

        if (sensitiveVariables.Count > 0)
        {
            password = Guid.NewGuid().ToString("N");

            var sensitiveJson = JsonSerializer.Serialize(sensitiveVariables);
            var encryption = new CalamariCompatibleEncryption(password);
            sensitiveBytes = encryption.Encrypt(sensitiveJson);
        }
        else
        {
            sensitiveBytes = Encoding.UTF8.GetBytes("{}");
        }

        return (variableJson, sensitiveBytes, password);
    }

    public static (Stream VariableJsonStream, Stream SensitiveVariableJsonStream, string Password) CreateVariableFileStreams(
        List<VariableDto> variables)
    {
        var (variableJson, sensitiveVariableJson, password) = CreateVariableFileContents(variables);

        return (new MemoryStream(variableJson), new MemoryStream(sensitiveVariableJson), password);
    }
}
