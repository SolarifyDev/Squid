using System.Text.RegularExpressions;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Channels;

public interface IChannelVersionRuleValidator : IScopedDependency
{
    List<ChannelVersionRuleViolation> Validate(IReadOnlyList<ChannelVersionRule> rules, IReadOnlyList<SelectedPackageInfo> packages);
}

public record SelectedPackageInfo(string ActionName, string Version);

public record ChannelVersionRuleViolation(string ActionName, string Version, string RuleSummary);

public class ChannelVersionRuleValidator : IChannelVersionRuleValidator
{
    public List<ChannelVersionRuleViolation> Validate(IReadOnlyList<ChannelVersionRule> rules, IReadOnlyList<SelectedPackageInfo> packages)
    {
        var violations = new List<ChannelVersionRuleViolation>();

        foreach (var package in packages)
        {
            var applicableRules = FindApplicableRules(rules, package.ActionName);
            if (applicableRules.Count == 0) continue;

            foreach (var rule in applicableRules)
            {
                if (!SatisfiesRule(package.Version, rule))
                    violations.Add(new ChannelVersionRuleViolation(package.ActionName, package.Version, FormatRuleSummary(rule)));
            }
        }

        return violations;
    }

    private static List<ChannelVersionRule> FindApplicableRules(IReadOnlyList<ChannelVersionRule> rules, string actionName)
    {
        var result = new List<ChannelVersionRule>();

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.ActionNames))
            {
                result.Add(rule);
                continue;
            }

            var actionNames = rule.ActionNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (actionNames.Any(a => string.Equals(a, actionName, StringComparison.OrdinalIgnoreCase)))
                result.Add(rule);
        }

        return result;
    }

    internal static bool SatisfiesRule(string version, ChannelVersionRule rule)
    {
        if (!TryParseVersion(version, out var semVer)) return true;

        if (!string.IsNullOrWhiteSpace(rule.VersionRange) && !SatisfiesVersionRange(semVer, rule.VersionRange))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.PreReleaseTag) && !SatisfiesPreReleaseTag(semVer.PreRelease, rule.PreReleaseTag))
            return false;

        return true;
    }

    internal static bool SatisfiesVersionRange(SemVer version, string rangeNotation)
    {
        if (!TryParseRange(rangeNotation, out var min, out var minInclusive, out var max, out var maxInclusive))
            return true;

        if (min != null)
        {
            var cmp = CompareVersions(version, min.Value);
            if (minInclusive ? cmp < 0 : cmp <= 0) return false;
        }

        if (max != null)
        {
            var cmp = CompareVersions(version, max.Value);
            if (maxInclusive ? cmp > 0 : cmp >= 0) return false;
        }

        return true;
    }

    internal static bool SatisfiesPreReleaseTag(string preRelease, string pattern)
    {
        if (string.IsNullOrEmpty(preRelease)) return false;

        try
        {
            return Regex.IsMatch(preRelease, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (RegexParseException)
        {
            return true;
        }
    }

    internal static bool TryParseVersion(string input, out SemVer result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var preReleaseIndex = input.IndexOf('-');
        var versionPart = preReleaseIndex >= 0 ? input[..preReleaseIndex] : input;
        var preRelease = preReleaseIndex >= 0 ? input[(preReleaseIndex + 1)..] : null;

        var parts = versionPart.Split('.');
        if (parts.Length < 2) return false;

        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return false;

        var patch = 0;
        if (parts.Length >= 3 && !int.TryParse(parts[2], out patch))
            return false;

        result = new SemVer(major, minor, patch, preRelease);
        return true;
    }

    internal static bool TryParseRange(string notation, out SemVer? min, out bool minInclusive, out SemVer? max, out bool maxInclusive)
    {
        min = null;
        max = null;
        minInclusive = false;
        maxInclusive = false;

        if (string.IsNullOrWhiteSpace(notation)) return false;

        notation = notation.Trim();

        // Exact version: "1.0.0" → [1.0.0, 1.0.0]
        if (notation[0] != '[' && notation[0] != '(')
        {
            if (TryParseVersion(notation, out var exact))
            {
                min = exact;
                max = exact;
                minInclusive = true;
                maxInclusive = true;
                return true;
            }
            return false;
        }

        // Interval notation: [1.0,2.0), (,3.0], etc.
        minInclusive = notation[0] == '[';
        maxInclusive = notation[^1] == ']';

        var inner = notation[1..^1];
        var commaIndex = inner.IndexOf(',');
        if (commaIndex < 0)
        {
            // Single value in brackets: [1.0.0] means exact match
            if (TryParseVersion(inner.Trim(), out var single))
            {
                min = single;
                max = single;
                return true;
            }
            return false;
        }

        var minStr = inner[..commaIndex].Trim();
        var maxStr = inner[(commaIndex + 1)..].Trim();

        if (!string.IsNullOrEmpty(minStr))
        {
            if (!TryParseVersion(minStr, out var minParsed)) return false;
            min = minParsed;
        }

        if (!string.IsNullOrEmpty(maxStr))
        {
            if (!TryParseVersion(maxStr, out var maxParsed)) return false;
            max = maxParsed;
        }

        return min != null || max != null;
    }

    internal static int CompareVersions(SemVer a, SemVer b)
    {
        var cmp = a.Major.CompareTo(b.Major);
        if (cmp != 0) return cmp;

        cmp = a.Minor.CompareTo(b.Minor);
        if (cmp != 0) return cmp;

        return a.Patch.CompareTo(b.Patch);
    }

    private static string FormatRuleSummary(ChannelVersionRule rule)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(rule.VersionRange))
            parts.Add($"version range {rule.VersionRange}");

        if (!string.IsNullOrWhiteSpace(rule.PreReleaseTag))
            parts.Add($"pre-release tag /{rule.PreReleaseTag}/");

        return parts.Count > 0 ? string.Join(", ", parts) : "channel version rule";
    }

    internal record struct SemVer(int Major, int Minor, int Patch, string PreRelease);
}
