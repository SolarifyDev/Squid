using System.Text;
using Newtonsoft.Json;
using Squid.Core.Services.Common;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Variable;

public interface IHybridVariableSnapshotService : IScopedDependency
{
    Task<VariableSetSnapshotDto> GetOrCreateSnapshotAsync(List<int> variableSetIds, string createdBy, CancellationToken cancellationToken = default);
    
    Task<VariableSetSnapshotDto> LoadSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default);
    
    Task<List<VariableSetSnapshotDto>> LoadSnapshotsAsync(List<int> snapshotIds, CancellationToken cancellationToken = default);

    Task<(VariableSetSnapshotDto snapshotData, string currentHash)> CalculateVariableLatestSnapshotAsync(List<int> variableSetIds, CancellationToken innerCancellationToken);
}

public class HybridVariableSnapshotService : IHybridVariableSnapshotService
{
    private readonly IMapper _mapper;
    private readonly IGenericDataProvider _genericDataProvider;
    private readonly IVariableDataProvider _variableDataProvider;
    private readonly IVariableSetSnapshotDataProvider _variableSetSnapshotDataProvider;

    public HybridVariableSnapshotService(
        IMapper mapper,
        IGenericDataProvider genericDataProvider,
        IVariableDataProvider variableDataProvider,
        IVariableSetSnapshotDataProvider variableSetSnapshotDataProvider)
    {
        _mapper = mapper;
        _variableDataProvider = variableDataProvider;
        _genericDataProvider = genericDataProvider;
        _variableSetSnapshotDataProvider = variableSetSnapshotDataProvider;
    }

    public async Task<VariableSetSnapshotDto> GetOrCreateSnapshotAsync(List<int> variableSetIds, string createdBy, CancellationToken cancellationToken = default)
    {
        return await _genericDataProvider.ExecuteInTransactionAsync(
            async innerCancellationToken =>
            {
                Log.Information("Creating snapshot for VariableSet {VariableSetId}", variableSetIds);
                
                var (snapshotData, currentHash) = await CalculateVariableLatestSnapshotAsync(variableSetIds, innerCancellationToken).ConfigureAwait(false);
                Log.Information("Content hash calculated: {Hash}", currentHash);

                var existingSnapshot = await _variableSetSnapshotDataProvider.GetExistingSnapshotAsync(currentHash, innerCancellationToken).ConfigureAwait(false);

                if (existingSnapshot != null)
                {
                    Log.Information("Reusing existing snapshot {SnapshotId}", existingSnapshot.Id);

                    return UtilService.DecompressFromGzip<VariableSetSnapshotDto>(existingSnapshot.SnapshotData);
                }
                
                var compressedData = UtilService.CompressToGzip(snapshotData);
                var uncompressedSize = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(snapshotData));

                var snapshot = new VariableSetSnapshot
                {
                    SnapshotData = compressedData,
                    ContentHash = currentHash,
                    CompressionType = "GZIP",
                    UncompressedSize = uncompressedSize,
                    CreatedBy = createdBy
                };

                await _variableSetSnapshotDataProvider.AddVariableSetSnapshotAsync(snapshot, false, innerCancellationToken).ConfigureAwait(false);

                Log.Information(
                    "Snapshot {SnapshotId} created successfully. " +
                    "Compressed size: {CompressedSize} bytes, " +
                    "Uncompressed size: {UncompressedSize} bytes, " +
                    "Compression ratio: {Ratio:P2}",
                    snapshot.Id, compressedData.Length, uncompressedSize,
                    1.0 - (double)compressedData.Length / uncompressedSize);

                return snapshotData;
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(VariableSetSnapshotDto snapshotData, string currentHash)> CalculateVariableLatestSnapshotAsync(List<int> variableSetIds, CancellationToken innerCancellationToken)
    {
        var snapshotData = await LoadCompleteVariableSetAsync(variableSetIds, innerCancellationToken).ConfigureAwait(false);
                
        var json = JsonConvert.SerializeObject(snapshotData);
        var currentHash = UtilService.ComputeSha256Hash(json);

        return (snapshotData, currentHash);
    }

    public async Task<VariableSetSnapshotDto> LoadSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _variableSetSnapshotDataProvider.GetVariableSetSnapshotByIdAsync(snapshotId, cancellationToken).ConfigureAwait(false);

        if (snapshot == null)
            throw new Exception($"Snapshot {snapshotId} not found");

        var snapshotData = UtilService.DecompressFromGzip<VariableSetSnapshotDto>(snapshot.SnapshotData);

        return snapshotData;
    }

    public async Task<List<VariableSetSnapshotDto>> LoadSnapshotsAsync(List<int> snapshotIds, CancellationToken cancellationToken = default)
    {
        var snapshots = await _variableSetSnapshotDataProvider.GetSnapshotsAsync(snapshotIds, cancellationToken).ConfigureAwait(false);
        
        if (snapshots.Count == 0) throw new Exception($"No snapshots found for {string.Join(',', snapshotIds)} variable sets");
        
        var snapshotData = snapshots.Select(
            x => UtilService.DecompressFromGzip<VariableSetSnapshotDto>(x.SnapshotData)).ToList();

        return snapshotData;
    }

    private async Task<VariableSetSnapshotDto> LoadCompleteVariableSetAsync(List<int> variableSetIds, CancellationToken cancellationToken)
    {
        var variableSets = await _variableDataProvider.GetVariableSetsByIdsAsync(variableSetIds, cancellationToken).ConfigureAwait(false);

        if (variableSets == null)
            throw new Exception($"VariableSet {variableSetIds} not found");
        
        var variables = await _variableDataProvider.GetVariablesByVariableSetIdsAsync(variableSetIds, cancellationToken).ConfigureAwait(false);
        
        return new VariableSetSnapshotDto
        {
            Variables = _mapper.Map<List<VariableDto>>(variables),
            CreatedBy = "System"
        };
    }
}
