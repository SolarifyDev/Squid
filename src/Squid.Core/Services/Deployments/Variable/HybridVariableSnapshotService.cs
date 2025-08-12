using Microsoft.Extensions.Logging;
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
    private readonly IVariableDataProvider _variableDataProvider;
    private readonly ISnapshotCompressionService _compressionService;
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    public HybridVariableSnapshotService(
        IVariableDataProvider variableDataProvider,
        ISnapshotCompressionService compressionService,
        IRepository repository,
        IUnitOfWork unitOfWork,
        ILogger<HybridVariableSnapshotService> logger)
    {
        _variableDataProvider = variableDataProvider;
        _compressionService = compressionService;
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<int> CreateSnapshotAsync(int variableSetId, string createdBy, CancellationToken cancellationToken = default)
    {
        Log.Information("Creating snapshot for VariableSet {VariableSetId}", variableSetId);

        using var transaction = await _repository.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var currentHash = await _variableDataProvider.CalculateContentHashAsync(variableSetId, cancellationToken);
            Log.Debug("Content hash calculated: {Hash}", currentHash);

            var existingSnapshot = await _repository.Query<VariableSetSnapshot>()
                .FirstOrDefaultAsync(s => s.OriginalVariableSetId == variableSetId && s.ContentHash == currentHash, cancellationToken);

            if (existingSnapshot != null)
            {
                Log.Information("Reusing existing snapshot {SnapshotId}", existingSnapshot.Id);
                await transaction.CommitAsync(cancellationToken);
                return existingSnapshot.Id;
            }

            var snapshotData = await LoadCompleteVariableSetAsync(variableSetId, cancellationToken);
            await EmbedScopeDefinitionsAsync(snapshotData, cancellationToken);

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
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = createdBy
            };

            await _repository.InsertAsync(snapshot, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            Log.Information("Snapshot {SnapshotId} created successfully. " +
                                 "Compressed size: {CompressedSize} bytes, " +
                                 "Uncompressed size: {UncompressedSize} bytes, " +
                                 "Compression ratio: {Ratio:P2}",
                                 snapshot.Id, compressedData.Length, uncompressedSize,
                                 1.0 - (double)compressedData.Length / uncompressedSize);

            return snapshot.Id;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create snapshot for VariableSet {VariableSetId}", variableSetId);
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<VariableSetSnapshotData> LoadSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _repository.Query<VariableSetSnapshot>()
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);

        if (snapshot == null)
            throw new Exception($"Snapshot {snapshotId} not found");

        var snapshotData = _compressionService.DecompressSnapshot(snapshot.SnapshotData);

        ValidateSnapshotIntegrity(snapshotData, snapshot);

        return snapshotData;
    }

    public async Task<List<int>> CreateSnapshotsForReleaseAsync(int releaseId, List<int> variableSetIds, string createdBy, CancellationToken cancellationToken = default)
    {
        var snapshotIds = new List<int>();

        using var transaction = await _repository.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var variableSetId in variableSetIds)
            {
                var snapshotId = await CreateSnapshotAsync(variableSetId, createdBy, cancellationToken);
                snapshotIds.Add(snapshotId);

                var releaseSnapshot = new ReleaseVariableSnapshot
                {
                    ReleaseId = releaseId,
                    VariableSetId = variableSetId,
                    SnapshotId = snapshotId,
                    VariableSetType = ReleaseVariableSetType.Project
                };

                await _repository.InsertAsync(releaseSnapshot, cancellationToken);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return snapshotIds;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create snapshots for Release {ReleaseId}", releaseId);
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<VariableSetSnapshotData> LoadCompleteVariableSetAsync(int variableSetId, CancellationToken cancellationToken)
    {
        var variableSet = await _variableDataProvider.GetVariableSetByIdAsync(variableSetId, cancellationToken);

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
