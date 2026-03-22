namespace Squid.Tentacle.ScriptExecution;

internal static class SensitiveOutputMasker
{
    private const string MaskToken = "***";
    private const int MinMaskLength = 3;

    internal static string MaskLine(string line, IReadOnlySet<string> sensitiveValues)
    {
        if (string.IsNullOrEmpty(line) || sensitiveValues == null || sensitiveValues.Count == 0)
            return line;

        foreach (var value in sensitiveValues)
        {
            if (value.Length < MinMaskLength) continue;

            if (line.Contains(value, StringComparison.Ordinal))
                line = line.Replace(value, MaskToken, StringComparison.Ordinal);
        }

        return line;
    }
}
