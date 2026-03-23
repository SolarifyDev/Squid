namespace Squid.Tentacle.ScriptExecution;

internal static class SensitiveOutputMasker
{
    private const string MaskToken = "********";
    private const int MinMaskLength = 4;

    internal static string MaskLine(string line, IReadOnlySet<string> sensitiveValues)
    {
        if (string.IsNullOrEmpty(line) || sensitiveValues == null || sensitiveValues.Count == 0)
            return line;

        var sorted = sensitiveValues
            .Where(v => v.Length >= MinMaskLength)
            .OrderByDescending(v => v.Length);

        foreach (var value in sorted)
        {
            if (line.Contains(value, StringComparison.Ordinal))
                line = line.Replace(value, MaskToken, StringComparison.Ordinal);
        }

        return line;
    }
}
