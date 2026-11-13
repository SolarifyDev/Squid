namespace Squid.Core.Services.OctopusImport.History;

/// <summary>
/// PostgreSQL text and jsonb reject U+0000, which Octopus logs carry when a script's tool writes UTF-16 output.
/// Every string the history importer writes drops it, so one such line cannot fail the whole import.
/// </summary>
public static class OctopusImportText
{
    public static string StripNul(string value) => value?.Replace("\0", string.Empty, StringComparison.Ordinal);

    public static object[] StripNul(IEnumerable<object> values) => values.Select(value => value is string text ? StripNul(text) : value).ToArray();
}
