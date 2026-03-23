namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public static class YamlSafeScalar
{
    public static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        return $"\"{escaped}\"";
    }
}
