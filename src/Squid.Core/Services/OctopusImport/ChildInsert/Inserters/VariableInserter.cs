using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.ChildInsert.Inserters;

/// <summary>
/// A variable added into an imported variable set: mapped with the same scope and secret rules as the whole set, appended without
/// re-saving the set. A sensitive value the Octopus server withheld is added empty and recorded as missing, as on a first import.
/// </summary>
public sealed class VariableInserter(IOctopusImportVariableMapper variableMapper, IVariableService variableService) : IOctopusImportChildInserter
{
    public OctopusResourceKind Kind => OctopusResourceKind.Variable;

    // Variables are always appended (their order carries no meaning), so their positions are never read.
    public Task<IReadOnlyDictionary<int, int>> SiblingOrdersAsync(int ownerId, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>());

    public async Task<OctopusImportInsertedChild> InsertAsync(OctopusImportChildInsert insert, CancellationToken ct)
    {
        var set = insert.Find(insert.Resource.ParentSourceId, OctopusResourceKind.VariableSet);
        var mapping = variableMapper.MapToUpdateCommand(set, insert.IdMap, insert.OwnerDestinationId, insert.SpaceId);
        var index = VariableIndex(set, insert.Resource.SourceId);
        var diagnostics = OctopusImportSiblingDiagnostics.Without(mapping.Diagnostics, ForeignIds(set, index));

        if (index < 0 || OctopusImportSiblingDiagnostics.HasBlocker(diagnostics)) return OctopusImportInsertedChild.Blocked(diagnostics);

        var ids = await variableService.AddVariablesAsync(insert.OwnerDestinationId, [mapping.UpdateCommand.Variables[index]], ct).ConfigureAwait(false);

        return new OctopusImportInsertedChild(ids.Single(), diagnostics, new Dictionary<string, int>());
    }

    private static IReadOnlyList<string> VariableIds(OctopusResourceNode set)
    {
        var source = set.GetSource<OctopusVariableSetDto>();

        return (source?.Variables ?? []).Select((variable, index) => OctopusResourceGraphBuilder.BuildChildSourceId(source.Id, "variable", variable.Id, index)).ToList();
    }

    private static int VariableIndex(OctopusResourceNode set, string variableSourceId) => OctopusImportChildPlacement.IndexOf(VariableIds(set), variableSourceId);

    // The ids (as Octopus gives them and as the graph names them) of every other variable of the set.
    private static IReadOnlySet<string> ForeignIds(OctopusResourceNode set, int index)
    {
        var variables = set.GetSource<OctopusVariableSetDto>()?.Variables ?? [];
        var graphIds = VariableIds(set);

        return variables.SelectMany((variable, i) => i == index ? [] : new[] { variable.Id, graphIds[i] }).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
