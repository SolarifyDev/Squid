using Microsoft.Extensions.Logging;
using Squid.Core.Services.Common;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Variable;

public interface IHybridVariableSnapshotService : IScopedDependency
{
    Task<int> GetOrCreateSnapshotAsync(int variableSetId, string createdBy, CancellationToken cancellationToken = default);
    
    Task<VariableSetSnapshotData> LoadSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default);
    
    Task<List<VariableSetSnapshotData>> LoadSnapshotsAsync(List<int> snapshotIds, CancellationToken cancellationToken = default);
    
    Task<List<int>> CreateSnapshotsForReleaseAsync(int releaseId, List<int> variableSetIds, string createdBy, CancellationToken cancellationToken = default);
}

public class HybridVariableSnapshotService : IHybridVariableSnapshotService
{
    private readonly IMapper _mapper;
    private readonly IGenericDataProvider _genericDataProvider;
    private readonly IVariableDataProvider _variableDataProvider;
    private readonly IVariableSetSnapshotDataProvider _snapshotDataProvider;
    private readonly IReleaseVariableSnapshotDataProvider _releaseSnapshotDataProvider;

    public HybridVariableSnapshotService(
        IMapper mapper,
        IGenericDataProvider genericDataProvider,
        IVariableDataProvider variableDataProvider,
        IVariableSetSnapshotDataProvider snapshotDataProvider,
        IReleaseVariableSnapshotDataProvider releaseSnapshotDataProvider)
    {
        _mapper = mapper;
        _variableDataProvider = variableDataProvider;
        _snapshotDataProvider = snapshotDataProvider;
        _genericDataProvider = genericDataProvider;
        _releaseSnapshotDataProvider = releaseSnapshotDataProvider;
    }

    public async Task<int> GetOrCreateSnapshotAsync(int variableSetId, string createdBy, CancellationToken cancellationToken = default)
    {
        return await _genericDataProvider.ExecuteInTransactionAsync<int>(
            async innerCancellationToken =>
            {
                Log.Information("Creating snapshot for VariableSet {VariableSetId}", variableSetId);

                var currentHash = await _variableDataProvider.CalculateContentHashAsync(variableSetId, innerCancellationToken).ConfigureAwait(false);
                Log.Information("Content hash calculated: {Hash}", currentHash);

                var existingSnapshot = await _snapshotDataProvider.GetExistingSnapshotAsync(variableSetId, currentHash, innerCancellationToken).ConfigureAwait(false);

                if (existingSnapshot != null)
                {
                    Log.Information("Reusing existing snapshot {SnapshotId}", existingSnapshot.Id);

                    return existingSnapshot.Id;
                }

                var snapshotData = await LoadCompleteVariableSetAsync(variableSetId, innerCancellationToken).ConfigureAwait(false);
                EmbedScopeDefinitionsAsync(snapshotData);

                var compressedData = SnapshotCompressionService.CompressSnapshot(snapshotData);
                var uncompressedSize = SnapshotCompressionService.EstimateUncompressedSize(snapshotData);

                var snapshot = new VariableSetSnapshot
                {
                    OriginalVariableSetId = variableSetId,
                    Version = snapshotData.Version,
                    SnapshotData = compressedData,
                    ContentHash = currentHash,
                    CompressionType = "GZIP",
                    UncompressedSize = uncompressedSize,
                    CreatedBy = createdBy
                };

                await _snapshotDataProvider.AddVariableSetSnapshotAsync(snapshot, false, innerCancellationToken).ConfigureAwait(false);

                Log.Information(
                    "Snapshot {SnapshotId} created successfully. " +
                    "Compressed size: {CompressedSize} bytes, " +
                    "Uncompressed size: {UncompressedSize} bytes, " +
                    "Compression ratio: {Ratio:P2}",
                    snapshot.Id, compressedData.Length, uncompressedSize,
                    1.0 - (double)compressedData.Length / uncompressedSize);

                return snapshot.Id;
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VariableSetSnapshotData> LoadSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _snapshotDataProvider.GetVariableSetSnapshotByIdAsync(snapshotId, cancellationToken).ConfigureAwait(false);

        if (snapshot == null)
            throw new Exception($"Snapshot {snapshotId} not found");

        var snapshotData = SnapshotCompressionService.DecompressSnapshot(snapshot.SnapshotData);

        ValidateSnapshotIntegrity(snapshotData, snapshot);

        return snapshotData;
    }

    public async Task<List<VariableSetSnapshotData>> LoadSnapshotsAsync(List<int> snapshotIds, CancellationToken cancellationToken = default)
    {
        var snapshots = await _snapshotDataProvider.GetSnapshotsAsync(snapshotIds, cancellationToken).ConfigureAwait(false);
        
        if (snapshots.Count == 0) throw new Exception($"No snapshots found for {string.Join(',', snapshotIds)} variable sets");
        
        var snapshotData = snapshots.Select(
            x =>
            {
                var data = SnapshotCompressionService.DecompressSnapshot(x.SnapshotData);
                ValidateSnapshotIntegrity(data, x);

                return data;
            }).ToList();

        return snapshotData;
    }

    public async Task<List<int>> CreateSnapshotsForReleaseAsync(int releaseId, List<int> variableSetIds, string createdBy, CancellationToken cancellationToken = default)
    {
        return await _genericDataProvider.ExecuteInTransactionAsync<List<int>>(
            async token =>
            {
                var snapshotIds = new List<int>();

                foreach (var variableSetId in variableSetIds)
                {
                    var snapshotId = await GetOrCreateSnapshotAsync(variableSetId, createdBy, token).ConfigureAwait(false);
                    snapshotIds.Add(snapshotId);

                    var releaseSnapshot = new ReleaseVariableSnapshot
                    {
                        ReleaseId = releaseId, VariableSetId = variableSetId, SnapshotId = snapshotId, VariableSetType = ReleaseVariableSetType.Project
                    };

                    await _releaseSnapshotDataProvider.AddReleaseVariableSnapshotAsync(releaseSnapshot, false, token).ConfigureAwait(false);
                }

                return snapshotIds;
            }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VariableSetSnapshotData> LoadCompleteVariableSetAsync(int variableSetId, CancellationToken cancellationToken)
    {
        var variableSet = await _variableDataProvider.GetVariableSetByIdAsync(variableSetId, cancellationToken).ConfigureAwait(false);

        if (variableSet == null)
            throw new Exception($"VariableSet {variableSetId} not found");
        
        var variables = await _variableDataProvider.GetVariablesByVariableSetIdAsync(variableSetId, cancellationToken).ConfigureAwait(false);

        return new VariableSetSnapshotData
        {
            Id = variableSet.Id,
            OwnerId = variableSet.OwnerId,
            OwnerType = variableSet.OwnerType,
            Version = variableSet.Version,
            Variables = _mapper.Map<List<VariableSnapshotData>>(variables),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private void EmbedScopeDefinitionsAsync(VariableSetSnapshotData snapshotData)
    {
        var usedScopes = snapshotData.Variables
            .SelectMany(v => v.Scopes)
            .GroupBy(s => s.ScopeType)
            .ToDictionary(g => g.Key.ToString(), g => g.Select(s => s.ScopeValue).Distinct().ToList());

        snapshotData.ScopeDefinitions = usedScopes;
    }

    private void ValidateSnapshotIntegrity(VariableSetSnapshotData data, VariableSetSnapshot snapshot)
    {
        if (data.Version != snapshot.Version)
            throw new Exception("Snapshot version mismatch");

        if (data.Variables.Count > 10000) throw new Exception("Suspicious variable count in snapshot");

        foreach (var variable in data.Variables)
        {
            if (string.IsNullOrEmpty(variable.Name))
                throw new Exception("Variable name cannot be empty");
        }
    }
}
