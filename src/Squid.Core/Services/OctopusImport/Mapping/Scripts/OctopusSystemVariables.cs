using System.Text.RegularExpressions;
using Squid.Message.Constants;

namespace Squid.Core.Services.OctopusImport.Mapping.Scripts;

/// <summary>
/// Single source of truth for Octopus built-in variable names that have a Squid equivalent.
/// Used by the import-time reference rewriter, the script compatibility preamble and the
/// variable reference validator, so the three can never disagree about what is "supported".
/// </summary>
public static partial class OctopusSystemVariables
{
    public static IReadOnlyDictionary<string, string> SquidEquivalents { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Octopus.Project.Id"] = SpecialVariables.Project.Id,
        ["Octopus.Project.Name"] = SpecialVariables.Project.Name,
        ["Octopus.Environment.Id"] = SpecialVariables.Environment.Id,
        ["Octopus.Environment.Name"] = SpecialVariables.Environment.Name,
        ["Octopus.Machine.Id"] = SpecialVariables.Machine.Id,
        ["Octopus.Machine.Name"] = SpecialVariables.Machine.Name,
        ["Octopus.Machine.Roles"] = SpecialVariables.Machine.Roles,
        ["Octopus.Deployment.Id"] = SpecialVariables.Deployment.Id,
        ["Octopus.Deployment.CreatedBy.Id"] = SpecialVariables.Deployment.CreatedById,
        ["Octopus.Deployment.CreatedBy.Username"] = SpecialVariables.Deployment.CreatedByUsername,
        ["Octopus.Deployment.CreatedBy.DisplayName"] = SpecialVariables.Deployment.CreatedByDisplayName,
        ["Octopus.Release.Number"] = SpecialVariables.Release.Number,
        [OctopusPropertyNames.ActionPackageId] = SpecialVariables.Action.PackageId,
        [OctopusPropertyNames.ActionPackageFeedId] = SpecialVariables.Action.PackageFeedId,
        [OctopusPropertyNames.ActionPackageVersion] = SpecialVariables.Action.PackageVersion
    };

    public static bool IsOctopusName(string name) => !string.IsNullOrWhiteSpace(name) && name.StartsWith("Octopus.", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetSquidName(string octopusName, out string squidName)
    {
        squidName = null;

        return !string.IsNullOrWhiteSpace(octopusName) && SquidEquivalents.TryGetValue(octopusName.Trim(), out squidName);
    }

    /// <summary>
    /// Rewrites mapped Octopus built-in names that appear INSIDE <c>#{...}</c> expressions (plain
    /// references, filters, <c>#{if}</c>/<c>#{unless}</c> conditions) to their Squid names. Unmapped
    /// <c>Octopus.*</c> names and anything outside an expression are left byte-for-byte unchanged.
    /// </summary>
    public static string RewriteReferences(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("#{", StringComparison.Ordinal)) return text;

        return ExpressionPattern().Replace(text, expression => OctopusNamePattern().Replace(expression.Value, ReplaceName));
    }

    /// <summary>
    /// Lists, distinct and sorted, every <c>Octopus.*</c> name inside a <c>#{...}</c> expression that has
    /// no Squid equivalent (so <see cref="RewriteReferences"/> leaves it and it stays unresolved on Squid).
    /// Indexed names such as <c>Octopus.Action[Build].Output.Tag</c> are reported whole.
    /// </summary>
    public static IReadOnlyList<string> FindUnmappedReferences(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("#{", StringComparison.Ordinal)) return [];

        return ExpressionPattern().Matches(text)
            .SelectMany(expression => OctopusReferencePattern().Matches(expression.Value))
            .Select(match => match.Value)
            .Where(name => !TryGetSquidName(name, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ReplaceName(Match match) => TryGetSquidName(match.Value, out var squidName) ? squidName : match.Value;

    [GeneratedRegex(@"#\{[^{}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex ExpressionPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])Octopus(?:\.[A-Za-z0-9_]+|\[[^\[\]{}]*\])+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OctopusReferencePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])Octopus(?:\.[A-Za-z0-9_]+)+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OctopusNamePattern();
}
