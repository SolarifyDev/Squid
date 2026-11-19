using System.Text.RegularExpressions;

namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// Canonical Octopus source ids. An Octopus export appends one constant 32-hex suffix to every id and every embedded
/// reference (<c>Projects-643-&lt;sfx&gt;</c>, also inside child ids such as <c>deploymentprocess-Projects-1-&lt;sfx&gt;/step-0</c>);
/// the REST API returns the bare id. Stripping it gives one key for both sources. No REST id, GUID or slug id ends in
/// a dash followed by exactly 32 hex characters, so stripping is a no-op for them.
/// </summary>
public static class OctopusImportSourceIds
{
    /// <summary>Also applied to stored rows by the <c>20261113_canonicalise_octopus_import_source_ids.sql</c> migration (pinned by test).</summary>
    public const string ExportSuffixPattern = "-[0-9A-F]{32}(?=$|/)";

    private static readonly Regex ExportSuffix = new(ExportSuffixPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SuffixedSpaceId = new("^Spaces-[0-9]+-(?<suffix>[0-9A-F]{32})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string StripExportSuffix(string id) => string.IsNullOrEmpty(id) ? id : ExportSuffix.Replace(id, string.Empty);

    /// <summary>The upper-case export suffix of a suffixed space id (<c>Spaces-1-&lt;sfx&gt;</c>), or null for a bare REST space id.</summary>
    public static string ExportSuffixOf(string spaceId)
    {
        var match = string.IsNullOrEmpty(spaceId) ? Match.Empty : SuffixedSpaceId.Match(spaceId);

        return match.Success ? match.Groups["suffix"].Value.ToUpperInvariant() : null;
    }
}
