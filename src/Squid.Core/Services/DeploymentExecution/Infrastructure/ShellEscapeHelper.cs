using System.Text;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public static class ShellEscapeHelper
{
    public static string Base64Encode(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    public static string EscapeBash(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("!", "\\!", StringComparison.Ordinal);
    }

    public static string EscapePowerShell(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        return value
            .Replace("`", "``", StringComparison.Ordinal)
            .Replace("\"", "`\"", StringComparison.Ordinal)
            .Replace("$", "`$", StringComparison.Ordinal);
    }
}
