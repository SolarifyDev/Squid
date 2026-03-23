namespace Squid.Core.Services.DeploymentExecution.Lifecycle;

public sealed class SensitiveValueMasker
{
    public const string MaskToken = "********";
    private const int MinValueLength = 4;

    private readonly string[] _sortedValues;

    public SensitiveValueMasker(IEnumerable<string> sensitiveValues)
    {
        _sortedValues = sensitiveValues
            .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length >= MinValueLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length)
            .ToArray();
    }

    public int ValueCount => _sortedValues.Length;

    public string Mask(string text)
    {
        if (string.IsNullOrEmpty(text) || _sortedValues.Length == 0) return text;

        foreach (var value in _sortedValues)
            text = text.Replace(value, MaskToken, StringComparison.Ordinal);

        return text;
    }
}
