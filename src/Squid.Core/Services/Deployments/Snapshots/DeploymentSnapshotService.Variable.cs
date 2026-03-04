using Squid.Core.Services.Common;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Snapshots;

public partial interface IDeploymentSnapshotService
{
    Task<VariableSetSnapshotDto> SnapshotVariableSetFromReleaseAsync(Persistence.Entities.Deployments.Release release, CancellationToken cancellationToken = default);
    
    Task<VariableSetSnapshotDto> SnapshotVariableSetFromIdsAsync(List<int> variableSetIds, CancellationToken cancellationToken = default);

    Task<VariableSetSnapshotDto> LoadVariableSetSnapshotAsync(int variableSetSnapshotId, CancellationToken cancellationToken = default);
}

public partial class DeploymentSnapshotService
{
    public async Task<VariableSetSnapshotDto> SnapshotVariableSetFromReleaseAsync(Persistence.Entities.Deployments.Release release, CancellationToken cancellationToken = default)
    {
        var project = await _projectDataProvider.GetProjectByIdAsync(release.ProjectId, cancellationToken).ConfigureAwait(false);

        return await SnapshotVariableSetFromIdsAsync(project.GetIncludedLibraryVariableSetIdList(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<VariableSetSnapshotDto> SnapshotVariableSetFromIdsAsync(List<int> variableSetIds, CancellationToken cancellationToken = default)
    {
        var variables = await _variableDataProvider
            .GetVariablesByVariableSetIdsAsync(variableSetIds, cancellationToken).ConfigureAwait(false);

        var snapshotData = await GenerateVariableSetSnapshotDataAsync(variables, cancellationToken).ConfigureAwait(false);
        var blob = UtilService.BuildSnapshotBlob(snapshotData);

        var existing = await _deploymentSnapshotDataProvider
            .GetExistingVariableSetSnapshotAsync(blob.ContentHash, cancellationToken).ConfigureAwait(false);

        if (existing != null)
        {
            return _mapper.Map<VariableSetSnapshotDto>(existing,
                opts => opts.AfterMap((_, dest) => dest.Data = snapshotData));
        }

        var variableSetSnapshot = BuildVariableSetSnapshot(blob);

        await _deploymentSnapshotDataProvider.AddVariableSetSnapshotAsync(variableSetSnapshot, cancellationToken: cancellationToken).ConfigureAwait(false);

        return _mapper.Map<VariableSetSnapshotDto>(variableSetSnapshot,
            opts => opts.AfterMap((_, dest) => dest.Data = snapshotData));
    }

    public async Task<VariableSetSnapshotDto> LoadVariableSetSnapshotAsync(int variableSetSnapshotId, CancellationToken cancellationToken = default)
    {
        var snapshotFromDb = await _deploymentSnapshotDataProvider.GetVariableSetSnapshotByIdAsync(variableSetSnapshotId, cancellationToken).ConfigureAwait(false);

        if (snapshotFromDb == null) throw new ArgumentNullException(nameof(snapshotFromDb));

        var snapshot = _mapper.Map<VariableSetSnapshotDto>(snapshotFromDb);
        
        snapshot.Data = UtilService.DecompressFromGzip<VariableSetSnapshotDataDto>(snapshotFromDb.SnapshotData);

        return snapshot;
    }

    private static VariableSetSnapshot BuildVariableSetSnapshot(SnapshotBlob blob)
    {
        return new VariableSetSnapshot
        {
            CreatedBy = "System",
            ContentHash = blob.ContentHash,
            SnapshotData = blob.CompressedData,
            UncompressedSize = blob.UncompressedSize,
            CompressionType = "GZIP"
        };
    }
    
    private async Task<VariableSetSnapshotDataDto> GenerateVariableSetSnapshotDataAsync(
        List<Variable> variables, CancellationToken ct)
    {
        var dtos = _mapper.Map<List<VariableDto>>(variables);

        var variableIds = variables.Select(v => v.Id).ToList();
        var allScopes = await _variableScopeDataProvider
            .GetVariableScopesByVariableIdsAsync(variableIds, ct).ConfigureAwait(false);

        var scopesByVariableId = allScopes
            .GroupBy(s => s.VariableId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var dto in dtos)
        {
            if (scopesByVariableId.TryGetValue(dto.Id, out var scopes))
                dto.Scopes = _mapper.Map<List<VariableScopeDto>>(scopes);
        }

        return new VariableSetSnapshotDataDto { Variables = dtos };
    }
}