using Squid.Core.Services.Deployments.Channels;

namespace Squid.Core.Services.Deployments.Releases.Exceptions;

public sealed class ReleaseVersionRuleViolationException : CreateReleaseAssertionException
{
    public int ChannelId { get; }

    public IReadOnlyList<ChannelVersionRuleViolation> Violations { get; }

    public ReleaseVersionRuleViolationException(int channelId, IReadOnlyList<ChannelVersionRuleViolation> violations)
        : base(FormatMessage(channelId, violations))
    {
        ChannelId = channelId;
        Violations = violations;
    }

    private static string FormatMessage(int channelId, IReadOnlyList<ChannelVersionRuleViolation> violations)
    {
        var details = string.Join("; ", violations.Select(v => $"action '{v.ActionName}' version '{v.Version}' violates {v.RuleSummary}"));
        return $"Release packages violate channel {channelId} version rules: {details}";
    }
}
