using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Variable;

/// <summary>
/// What identifies one variable inside its variable set when its id cannot: name, type and scope set. Squid re-inserts every
/// variable of a set on each save (new ids), so a value that must be found again later — e.g. a secret imported empty and
/// filled afterwards — is located by this identity. Scopes are <c>Type:Value</c> tokens, distinct and ordinal-sorted, so the
/// same scope set always compares equal whatever order it was stored in.
/// </summary>
public sealed record VariableIdentity(string Name, VariableType Type, IReadOnlyList<string> Scopes)
{
    public static string ScopeToken(VariableScopeType type, string value) => $"{type}:{value?.Trim()}";

    public static VariableIdentity Of(string name, VariableType type, IEnumerable<(VariableScopeType Type, string Value)> scopes)
        => new(name, type, Canonical((scopes ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Value)).Select(s => ScopeToken(s.Type, s.Value))));

    public static IReadOnlyList<string> Canonical(IEnumerable<string> tokens) => (tokens ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    public bool Matches(VariableIdentity other)
        => other != null && string.Equals(Name, other.Name, StringComparison.Ordinal) && Type == other.Type && (Scopes ?? []).SequenceEqual(other.Scopes ?? [], StringComparer.Ordinal);
}
