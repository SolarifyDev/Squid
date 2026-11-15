using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Variables;

/// <summary>
/// A read of a variable set returns every sensitive value as <see cref="Mask"/>, and the Variables page posts the set back as read.
/// A save therefore keeps the value Squid stores for each masked secret — including an empty one, so a secret the Octopus API
/// withheld stays fillable and keeps failing the deployment preflight — and never stores the mask itself.
/// </summary>
public static class MaskedSensitiveValues
{
    public const string Mask = "***SENSITIVE***";

    public static bool IsMasked(VariableModel variable) => variable.IsSensitive && string.Equals(variable.Value, Mask, StringComparison.Ordinal);

    public static bool AnyMasked(IEnumerable<VariableModel> variables) => (variables ?? []).Any(IsMasked);

    /// <summary>
    /// Gives each masked secret the stored value of the same variable: same identity first, else the only unclaimed stored secret
    /// with that name (its scope was edited). Anything else — none, or several to choose from — is saved empty.
    /// </summary>
    public static void Restore(IReadOnlyList<VariableModel> incoming, IReadOnlyList<(VariableIdentity Identity, string Value)> stored)
    {
        var unclaimed = (stored ?? []).ToList();
        var masked = (incoming ?? []).Where(IsMasked).ToList();

        var unmatched = masked.Where(v => !TryClaim(unclaimed, v, unclaimed.Where(s => IdentityOf(v).Matches(s.Identity)).Take(1))).ToList();

        foreach (var variable in unmatched)
            if (!TryClaim(unclaimed, variable, SingleByName(unclaimed, variable.Name))) variable.Value = string.Empty;
    }

    private static IEnumerable<(VariableIdentity Identity, string Value)> SingleByName(List<(VariableIdentity Identity, string Value)> unclaimed, string name)
    {
        var sameName = unclaimed.Where(s => string.Equals(s.Identity.Name, name, StringComparison.Ordinal)).ToList();

        return sameName.Count == 1 ? sameName : [];
    }

    private static bool TryClaim(List<(VariableIdentity Identity, string Value)> unclaimed, VariableModel variable, IEnumerable<(VariableIdentity Identity, string Value)> candidates)
    {
        var match = candidates.ToList();

        if (match.Count == 0) return false;

        unclaimed.Remove(match[0]);
        variable.Value = match[0].Value;

        return true;
    }

    private static VariableIdentity IdentityOf(VariableModel variable)
        => VariableIdentity.Of(variable.Name, variable.Type, (variable.Scopes ?? []).Select(s => (s.ScopeType, s.ScopeValue)));
}
