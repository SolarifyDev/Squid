using System.Text;
using Newtonsoft.Json;
using Squid.Core.Services.Common;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Snapshots;

public partial interface IDeploymentSnapshotService
{
    Task<VariableSetSnapshot> SnapshotVariableSetFromReleaseAsync(Persistence.Entities.Deployments.Release release, CancellationToken cancellationToken = default);
    
    Task<VariableSetSnapshot> SnapshotVariableSetFromIdsAsync(List<int> variableSetIds, CancellationToken cancellationToken = default);

    Task<VariableSetSnapshotDto> LoadVariableSetSnapshotAsync(int variableSetSnapshotId, CancellationToken cancellationToken = default);
}

public partial class DeploymentSnapshotService
{
    public async Task<VariableSetSnapshot> SnapshotVariableSetFromReleaseAsync(Persistence.Entities.Deployments.Release release, CancellationToken cancellationToken = default)
    {
        var project = await _projectDataProvider.GetProjectByIdAsync(release.ProjectId, cancellationToken).ConfigureAwait(false);

        return await SnapshotVariableSetFromIdsAsync(project.GetIncludedLibraryVariableSetIdList(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<VariableSetSnapshot> SnapshotVariableSetFromIdsAsync(List<int> variableSetIds, CancellationToken cancellationToken = default)
    {
        var variables = await _variableDataProvider
            .GetVariablesByVariableSetIdsAsync(variableSetIds, cancellationToken).ConfigureAwait(false);
        
        var snapshotData = GenerateVariableSetSnapshotData(variables);

        var variableSetSnapshot = BuildVariableSetSnapshot(snapshotData);
        
        await _deploymentSnapshotDataProvider.AddVariableSetSnapshotAsync(variableSetSnapshot, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        return variableSetSnapshot;
    }

    public async Task<VariableSetSnapshotDto> LoadVariableSetSnapshotAsync(int variableSetSnapshotId, CancellationToken cancellationToken = default)
    {
        var snapshotFromDb = await _deploymentSnapshotDataProvider.GetVariableSetSnapshotByIdAsync(variableSetSnapshotId, cancellationToken).ConfigureAwait(false);

        if (snapshotFromDb == null) throw new ArgumentNullException(nameof(snapshotFromDb));

        var snapshot = _mapper.Map<VariableSetSnapshotDto>(snapshotFromDb);
        
        snapshot.Data = UtilService.DecompressFromGzip<VariableSetSnapshotDataDto>(snapshotFromDb.SnapshotData);

        return snapshot;
    }

    private VariableSetSnapshot BuildVariableSetSnapshot(VariableSetSnapshotDataDto snapshotData)
    {
        var compressedData = UtilService.CompressToGzip(snapshotData);
        var uncompressedSize = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(snapshotData));
        var contentHash = UtilService.ComputeSha256Hash(JsonConvert.SerializeObject(snapshotData));
        
        return new VariableSetSnapshot
        {
            CreatedBy = "System",
            ContentHash = contentHash,
            SnapshotData = compressedData,
            UncompressedSize = uncompressedSize,
            CompressionType = "GZIP"
        };
    }
    
    private VariableSetSnapshotDataDto GenerateVariableSetSnapshotData(List<Variable> variables)
    {
        return new VariableSetSnapshotDataDto
        {
            Variables = _mapper.Map<List<VariableDto>>(variables)
        };
    }
}