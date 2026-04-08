using System.Text;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public static class SshBootstrapper
{
    internal const string EnvSquidHome = "SquidHome";
    internal const string EnvSquidWorkDir = "SquidWorkDirectory";
    internal const string EnvSquidTaskId = "SquidServerTaskId";

    public static string WrapBashScript(string scriptBody, string workDir, int serverTaskId, string baseDir)
    {
        var sb = new StringBuilder();

        sb.AppendLine("#!/bin/bash");
        sb.AppendLine($"export {EnvSquidHome}=\"{baseDir}\"");
        sb.AppendLine($"export {EnvSquidWorkDir}=\"{workDir}\"");
        sb.AppendLine($"export {EnvSquidTaskId}=\"{serverTaskId}\"");
        sb.AppendLine();
        sb.Append(scriptBody ?? string.Empty);

        return sb.ToString();
    }

    public static string WrapWithVariableExports(string scriptBody, List<VariableDto> variables, string workDir, int serverTaskId, string baseDir)
    {
        var sb = new StringBuilder();

        sb.AppendLine("#!/bin/bash");
        sb.AppendLine($"export {EnvSquidHome}=\"{baseDir}\"");
        sb.AppendLine($"export {EnvSquidWorkDir}=\"{workDir}\"");
        sb.AppendLine($"export {EnvSquidTaskId}=\"{serverTaskId}\"");

        if (variables != null)
        {
            foreach (var variable in variables)
            {
                if (variable.IsSensitive) continue;
                if (string.IsNullOrEmpty(variable.Name)) continue;

                var envName = SanitizeEnvVarName(variable.Name);
                var escapedValue = EscapeBashValue(variable.Value ?? string.Empty);

                sb.AppendLine($"export {envName}=\"{escapedValue}\"");
            }
        }

        sb.AppendLine();
        sb.Append(scriptBody ?? string.Empty);

        return sb.ToString();
    }

    internal static string SanitizeEnvVarName(string name)
    {
        var sb = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }

        if (sb.Length > 0 && char.IsDigit(sb[0]))
            sb.Insert(0, '_');

        return sb.ToString();
    }

    internal static string EscapeBashValue(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("$", "\\$")
            .Replace("`", "\\`")
            .Replace("!", "\\!");
    }
}
