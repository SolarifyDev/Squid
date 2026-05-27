namespace Squid.Calamari.Commands.StructuredConfig;

/// <summary>
/// PR-3 — file-extension → <see cref="IStructuredConfigFormat"/> dispatch.
/// Iterates registered formats and returns the first match. Order is
/// curated so the most-specific extension wins (no compound suffixes in
/// this domain unlike package archives — pure single-extension dispatch).
///
/// <para>Operator-visible supported extensions are exposed via
/// <see cref="SupportedExtensions"/> for error messages + drift tests.</para>
/// </summary>
internal static class StructuredConfigFormatRegistry
{
    private static readonly IReadOnlyList<IStructuredConfigFormat> Formats = new IStructuredConfigFormat[]
    {
        new JsonConfigFormat(),
        new YamlConfigFormat(),
        new XmlConfigFormat()
    };

    /// <summary>Operator-facing extension list. Used in log lines when a
    /// matched file doesn't have a known structured-config extension.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } = new[]
    {
        ".json", ".json5",
        ".yaml", ".yml",
        ".xml"
    };

    public static IStructuredConfigFormat? Resolve(string filePath)
        => Formats.FirstOrDefault(f => f.CanHandle(filePath));
}
