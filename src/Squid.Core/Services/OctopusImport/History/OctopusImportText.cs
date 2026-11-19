namespace Squid.Core.Services.OctopusImport.History;

/// <summary>
/// PostgreSQL text and jsonb reject U+0000, which Octopus logs carry when a script's tool writes UTF-16 output.
/// Every string the history importer writes drops it, so one such line cannot fail the whole import.
/// </summary>
public static class OctopusImportText
{
    public static string StripNul(string value) => value?.Replace("\0", string.Empty, StringComparison.Ordinal);

    /// <summary>Cuts to at most <paramref name="maxLength"/> UTF-16 units without splitting a surrogate pair, which Npgsql's strict UTF-8 encoder rejects.</summary>
    public static string Truncate(string value, int maxLength)
    {
        if (value == null || value.Length <= maxLength) return value;

        var cut = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;

        return value[..cut];
    }

    public static object[] StripNul(IEnumerable<object> values) => values.Select(value => value is string text ? StripNul(text) : value).ToArray();
}
