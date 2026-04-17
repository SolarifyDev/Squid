using System.Text.RegularExpressions;

namespace Squid.Tentacle.Security.Admission;

/// <summary>
/// Agent-side admission policy. Evaluated before a <c>StartScript</c> reaches
/// the executor so the agent can refuse to run scripts that violate
/// locally-managed safety rules — destructive commands, non-whitelisted shells,
/// wrong isolation level for a sensitive mutex, etc.
///
/// This is a last-line-of-defence on top of server-side authorization. In
/// high-compliance deployments it gives the on-host operator a hard veto that
/// can't be bypassed by a compromised server.
/// </summary>
public sealed class AdmissionPolicy
{
    public int Version { get; init; } = 1;
    public List<AdmissionRule> Rules { get; init; } = new();

    public AdmissionDecision Evaluate(AdmissionContext context)
    {
        if (context == null) return AdmissionDecision.Allow();

        foreach (var rule in Rules)
        {
            var match = rule.Evaluate(context);
            if (match != null) return match;
        }

        return AdmissionDecision.Allow();
    }

    public static AdmissionPolicy Empty() => new();
}

public sealed class AdmissionRule
{
    public string Id { get; init; } = string.Empty;
    public List<string> DenyScriptBodyRegex { get; init; } = new();
    public List<string> WhenIsolationMutexName { get; init; } = new();
    public string? RequireIsolationLevel { get; init; }
    public string Message { get; init; } = "denied by admission policy";

    private List<Regex>? _compiled;
    private List<Regex> CompiledDenyRegexes => _compiled ??= DenyScriptBodyRegex
        .Select(pattern => new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
        .ToList();

    internal AdmissionDecision? Evaluate(AdmissionContext context)
    {
        if (WhenIsolationMutexName.Count > 0 && !WhenIsolationMutexName.Contains(context.IsolationMutexName ?? string.Empty))
            return null;

        foreach (var rx in CompiledDenyRegexes)
        {
            if (rx.IsMatch(context.ScriptBody ?? string.Empty))
                return AdmissionDecision.Deny(Id, Message);
        }

        if (!string.IsNullOrEmpty(RequireIsolationLevel) && context.IsolationLevel != RequireIsolationLevel)
            return AdmissionDecision.Deny(Id, Message);

        return null;
    }
}

public sealed record AdmissionContext(
    string ScriptBody,
    string IsolationLevel,
    string? IsolationMutexName);

public sealed record AdmissionDecision(bool Allowed, string? RuleId, string? Reason)
{
    public static AdmissionDecision Allow() => new(true, null, null);
    public static AdmissionDecision Deny(string ruleId, string reason) => new(false, ruleId, reason);
}
