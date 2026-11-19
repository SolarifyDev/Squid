using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>Finds the sensitive values a deployment would use empty because an Octopus API import recorded them as missing (D16).</summary>
public interface IImportedSecretPreflight : IScopedDependency
{
    /// <summary>
    /// The missing values among <paramref name="variables"/> (the deployment's variables after prompted values are merged), each with
    /// the name of the project or library variable set that owns it. Empty when none — and without touching the database when the
    /// deployment has no empty sensitive value at all.
    /// </summary>
    Task<IReadOnlyList<MissingImportedSecret>> FindMissingAsync(int spaceId, IReadOnlyList<VariableDto> variables, VariableScopeContext deployment, CancellationToken ct = default);
}

public sealed class ImportedSecretPreflight(IOctopusImportSourceMapDataProvider sourceMap, IVariableDataProvider variableDataProvider, IRepository repository) : IImportedSecretPreflight
{
    public async Task<IReadOnlyList<MissingImportedSecret>> FindMissingAsync(int spaceId, IReadOnlyList<VariableDto> variables, VariableScopeContext deployment, CancellationToken ct = default)
    {
        var candidates = ImportedSecretPreflightRules.Candidates(variables, deployment);

        if (candidates.Count == 0) return [];

        var setIds = candidates.Select(c => c.VariableSetId).Distinct().ToList();
        var rows = await sourceMap.GetByDestinationsAsync(spaceId, ImportedSecretPreflightRules.SourceTypes, setIds, ct).ConfigureAwait(false);

        if (!rows.Any(r => r.SourceType == OctopusImportSourceMapQueries.VariableSecretSourceType)) return [];

        var live = await variableDataProvider.GetSensitiveVariableSlotsAsync(setIds, ct).ConfigureAwait(false);
        var missing = ImportedSecretPreflightRules.Match(candidates, rows, live, deployment);

        return missing.Count == 0 ? missing : await WithOwnerNamesAsync(missing, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MissingImportedSecret>> WithOwnerNamesAsync(IReadOnlyList<MissingImportedSecret> missing, CancellationToken ct)
    {
        var owners = await OwnerNamesAsync(missing.Select(m => m.VariableSetId).Distinct().ToList(), ct).ConfigureAwait(false);

        return missing.Select(m => m with { OwnerName = owners.GetValueOrDefault(m.VariableSetId) }).ToList();
    }

    // A set is owned through the owner's pointer (a project's or a library variable set's), the same rule the source map uses.
    private async Task<Dictionary<int, string>> OwnerNamesAsync(List<int> setIds, CancellationToken ct)
    {
        var projects = await repository.QueryNoTracking<Project>(p => setIds.Contains(p.VariableSetId)).Select(p => new { p.VariableSetId, p.Name }).ToListAsync(ct).ConfigureAwait(false);
        var libraries = await repository.QueryNoTracking<LibraryVariableSet>(l => setIds.Contains(l.VariableSetId)).Select(l => new { l.VariableSetId, l.Name }).ToListAsync(ct).ConfigureAwait(false);

        return projects.Select(p => (p.VariableSetId, Name: $"project \"{p.Name}\"")).Concat(libraries.Select(l => (l.VariableSetId, Name: $"library variable set \"{l.Name}\""))).GroupBy(o => o.VariableSetId).ToDictionary(g => g.Key, g => g.First().Name);
    }
}
