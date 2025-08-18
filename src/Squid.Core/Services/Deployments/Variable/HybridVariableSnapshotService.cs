using Microsoft.Extensions.Logging;
using Squid.Core.Services.Common;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments.Variable;

public interface IHybridVariableSnapshotService : IScopedDependency
{
    Task<int> CreateSnapshotAsync(int variableSetId, string createdBy, CancellationToken cancellationToken = default);
    
    Task<VariableSetSnapshotData> LoadSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default);
    
    Task<List<int>> CreateSnapshotsForReleaseAsync(int releaseId, List<int> variableSetIds, string createdBy, CancellationToken cancellationToken = default);
}

public class HybridVariableSnapshotService : IHybridVariableSnapshotService
{
    private readonly IGenericDataProvider _genericDataProvider;
    private readonly IVariableDataProvider _variableDataProvider;
    private readonly IVariableSetSnapshotDataProvider _snapshotDataProvider;
    private readonly IReleaseVariableSnapshotDataProvider _releaseSnapshotDataProvider;
    private readonly ISnapshotCompressionService _compressionService;

    public HybridVariableSnapshotService(
        IGenericDataProvider genericDataProvider,
        IVariableDataProvider variableDataProvider,
        IVariableSetSnapshotDataProvider snapshotDataProvider,
        IReleaseVariableSnapshotDataProvider releaseSnapshotDataProvider,
        ISnapshotCompressionService compressionService)
    {
        _variableDataProvider = variableDataProvider;
        _snapshotDataProvider = snapshotDataProvider;
        _releaseSnapshotDataProvider = releaseSnapshotDataProvider;
        _compressionService = compressionService;
        _genericDataProvider = genericDataProvider;
    }

    public async Task<int> CreateSnapshotAsync(int variableSetId, string createdBy, CancellationToken cancellationToken = default)
    {
        return await _genericDataProvider.ExecuteInTransactionAsync<int>(
            async token =>
            {
                Log.Information("Creating snapshot for VariableSet {VariableSetId}", variableSetId);

                var currentHash = await _variableDataProvider.CalculateContentHashAsync(variableSetId, token).ConfigureAwait(false);
                Log.Information("Content hash calculated: {Hash}", currentHash);

                var existingSnapshot = await _snapshotDataProvider.GetExistingSnapshotAsync(variableSetId, currentHash, token).ConfigureAwait(false);

                if (existingSnapshot != null)
                {
                    Log.Information("Reusing existing snapshot {SnapshotId}", existingSnapshot.Id);

                    return existingSnapshot.Id;
                }

                var snapshotData = await LoadCompleteVariableSetAsync(variableSetId, token).ConfigureAwait(false);
                await EmbedScopeDefinitionsAsync(snapshotData, token).ConfigureAwait(false);

                var compressedData = _compressionService.CompressSnapshot(snapshotData);
                var uncompressedSize = _compressionService.EstimateUncompressedSize(snapshotData);

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

                await _snapshotDataProvider.AddVariableSetSnapshotAsync(snapshot, false, token).ConfigureAwait(false);

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

        var snapshotData = _compressionService.DecompressSnapshot(snapshot.SnapshotData);

        ValidateSnapshotIntegrity(snapshotData, snapshot);

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
                    var snapshotId = await CreateSnapshotAsync(variableSetId, createdBy, token).ConfigureAwait(false);
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

        return new VariableSetSnapshotData
        {
            Id = variableSet.Id,
            OwnerId = variableSet.OwnerId,
            OwnerType = variableSet.OwnerType,
            Version = variableSet.Version,
            CreatedAt = DateTime.UtcNow
        };
    }

    private Task EmbedScopeDefinitionsAsync(VariableSetSnapshotData snapshotData, CancellationToken cancellationToken)
    {
        var usedScopes = snapshotData.Variables
            .SelectMany(v => v.Scopes)
            .GroupBy(s => s.ScopeType)
            .ToDictionary(g => g.Key.ToString(), g => g.Select(s => s.ScopeValue).Distinct().ToList());

        snapshotData.ScopeDefinitions = usedScopes;

        return Task.CompletedTask;
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
