using System.Text;
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
    /// references, filters, <c>#{if}</c>/<c>#{unless}</c> conditions, <c>#{each}</c> collections) to their
    /// Squid names. Expressions are located with Squid's own template parser
    /// (<see cref="OctopusTemplateSpans"/>), so escaped text (<c>##{Octopus.X}</c>, which evaluates to the
    /// literal <c>#{Octopus.X}</c>), quoted literals inside an expression, unmapped <c>Octopus.*</c> names,
    /// anything outside an expression, and text the engine cannot parse at all are left byte-for-byte
    /// unchanged — the rewrite never changes what the engine evaluates the text to.
    /// </summary>
    public static string RewriteReferences(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("#{", StringComparison.Ordinal)) return text;

        if (!OctopusTemplateSpans.TryParse(text, out var spans)) return text;

        var rewritten = new StringBuilder(text.Length);

        AppendRewritten(rewritten, spans);

        return rewritten.ToString();
    }

    /// <summary>
    /// Lists, distinct and sorted, every <c>Octopus.*</c> name inside a <c>#{...}</c> expression that has
    /// no Squid equivalent (so <see cref="RewriteReferences"/> leaves it and it stays unresolved on Squid).
    /// Indexed names such as <c>Octopus.Action[Build].Output.Tag</c> are reported whole. Expressions are
    /// located with the same template parser as the rewriter, so escaped text (<c>##{Octopus.X}</c>) and
    /// quoted literals are never reported; text the engine cannot parse is scanned as a whole.
    /// </summary>
    public static IReadOnlyList<string> FindUnmappedReferences(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("#{", StringComparison.Ordinal)) return [];

        var expressions = OctopusTemplateSpans.TryParse(text, out var spans) ? CollectExpressions(spans) : [text];

        return expressions
            .SelectMany(expression => OctopusReferencePattern().Matches(expression))
            .Where(IsReference)
            .Select(match => match.Groups[ReferenceGroup].Value)
            .Where(name => !TryGetSquidName(name, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private const string ReferenceGroup = "reference";

    // The pattern matches a double-quoted Octostache string literal as a whole first, so a name inside a
    // literal (`Replace "Octopus.Old" "x"`) is text, not a reference.
    private static bool IsReference(Match match) => match.Groups[ReferenceGroup].Success;

    private static List<string> CollectExpressions(IReadOnlyList<OctopusTemplateSpan> spans)
    {
        var expressions = new List<string>();

        foreach (var span in spans)
        {
            if (span.Kind == OctopusTemplateSpanKind.Substitution) expressions.Add(span.Raw);
            if (span.Kind != OctopusTemplateSpanKind.Block) continue;

            expressions.Add(span.OpenTag);
            expressions.AddRange(CollectExpressions(span.Children));
        }

        return expressions;
    }

    private static void AppendRewritten(StringBuilder output, IReadOnlyList<OctopusTemplateSpan> spans)
    {
        foreach (var span in spans)
        {
            switch (span.Kind)
            {
                case OctopusTemplateSpanKind.Substitution:
                    output.Append(RewriteExpression(span.Raw));
                    break;
                case OctopusTemplateSpanKind.Block:
                    output.Append(RewriteExpression(span.OpenTag));
                    AppendRewritten(output, span.Children);
                    output.Append(span.CloseTag);
                    break;
                default:
                    output.Append(span.Raw);
                    break;
            }
        }
    }

    // Quoted option / comparison literals ("…") are data, not variable names.
    private static string RewriteExpression(string expression)
        => ExpressionPartPattern().Replace(expression, part => part.Value.StartsWith('"') ? part.Value : OctopusNamePattern().Replace(part.Value, ReplaceName));

    private static string ReplaceName(Match match) => TryGetSquidName(match.Value, out var squidName) ? squidName : match.Value;

    [GeneratedRegex(@"""[^""]*""?|[^""]+", RegexOptions.CultureInvariant)]
    private static partial Regex ExpressionPartPattern();

    [GeneratedRegex(@"""[^""]*""|(?<reference>(?<![A-Za-z0-9_.])Octopus(?:\.[A-Za-z0-9_]+|\[[^\[\]{}]*\])+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OctopusReferencePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])Octopus(?:\.[A-Za-z0-9_]+)+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OctopusNamePattern();
}
