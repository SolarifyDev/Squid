using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Which Octopus variables of one set a Squid set's rows still hold. Variables may share a name and differ by scope (the usual Octopus
/// pattern), so a row answers for at most ONE Octopus variable: rows of the same name and scope pair first, then rows of the same name.
/// Rows the source map links to another Octopus variable are never passed in (they are that variable's). The same rule decides what the
/// preview offers, what confirmation refuses under the lock, and which row a variable written with its set was imported as.
/// </summary>
public static class OctopusImportVariableMatching
{
    /// <summary>The Squid row each paired Octopus variable is held by, keyed by the variable's source id.</summary>
    public static IReadOnlyDictionary<string, int> Pair(IEnumerable<OctopusResourceNode> variables, IEnumerable<OctopusImportDestinationChild> rows, Func<string, OctopusResourceKind, int?> destinationOf)
    {
        // A fixed order (source id, row id), so the preview and confirmation pair the same way whatever order they read in.
        var candidates = variables.OrderBy(v => v.SourceId, StringComparer.OrdinalIgnoreCase).Select(v => Candidate(v, destinationOf)).ToList();
        var free = rows.OrderBy(r => r.Id).ToList();
        var paired = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        PairWhere(candidates, free, paired, SameNameAndScope);
        PairWhere(candidates, free, paired, (candidate, row) => candidate.Name == row.Name);

        return paired;
    }

    private static void PairWhere(IReadOnlyList<VariableCandidate> candidates, List<OctopusImportDestinationChild> free, Dictionary<string, int> paired, Func<VariableCandidate, OctopusImportDestinationChild, bool> matches)
    {
        foreach (var candidate in candidates.Where(c => !paired.ContainsKey(c.SourceId)))
        {
            var row = free.FirstOrDefault(r => matches(candidate, r));

            if (row == null) continue;

            paired[candidate.SourceId] = row.Id;
            free.Remove(row);
        }
    }

    // A scope that cannot be mapped yet is compared by name only.
    private static bool SameNameAndScope(VariableCandidate candidate, OctopusImportDestinationChild row)
        => candidate.Name == row.Name && candidate.Scopes != null && row.Scopes != null && candidate.Scopes.SequenceEqual(row.Scopes, StringComparer.Ordinal);

    private static VariableCandidate Candidate(OctopusResourceNode variable, Func<string, OctopusResourceKind, int?> destinationOf)
    {
        var source = variable.GetSource<OctopusVariableDto>();

        return new VariableCandidate(variable.SourceId, source?.Name ?? variable.Name, source == null ? null : OctopusImportSecretKeys.CanonicalScopes(source.Scope, destinationOf));
    }

    private sealed record VariableCandidate(string SourceId, string Name, IReadOnlyList<string> Scopes);
}
