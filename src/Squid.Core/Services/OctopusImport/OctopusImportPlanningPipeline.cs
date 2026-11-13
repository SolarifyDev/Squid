using System.Collections.Concurrent;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportPlanningPipeline : IScopedDependency
{
    Task<OctopusImportPlanningSnapshot> BuildPreviewAsync(
        string temporaryUploadPath,
        int destinationSpaceId,
        CancellationToken ct = default);
}

public sealed record OctopusImportPlanningSnapshot(
    OctopusResourceGraph Graph,
    OctopusImportDependencyPlan DependencyPlan,
    OctopusImportConflictDiscoveryResult Conflicts,
    OctopusImportPreviewPlanDto PreviewPlan);

public class OctopusImportPlanningPipeline(
    IOctopusImportSourceReader sourceReader,
    IOctopusManifestInventoryBuilder inventoryBuilder,
    IOctopusResourceGraphBuilder graphBuilder,
    IOctopusImportDependencyPlanner dependencyPlanner,
    IOctopusImportConflictDiscoveryService conflictDiscoveryService,
    IOctopusImportPreviewPlanner previewPlanner)
    : IOctopusImportPlanningPipeline
{
    private readonly ConcurrentDictionary<ExtractionCacheKey, Lazy<Task<OctopusInputExtractionResult>>> _extractions = new();

    public async Task<OctopusImportPlanningSnapshot> BuildPreviewAsync(
        string temporaryUploadPath,
        int destinationSpaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(temporaryUploadPath))
            throw new ArgumentException("Temporary upload path is required.", nameof(temporaryUploadPath));
        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        var extraction = await ExtractInputAsync(temporaryUploadPath, ct).ConfigureAwait(false);
        var inventory = inventoryBuilder.Build(extraction);
        var graph = graphBuilder.Build(inventory);
        var dependencyPlan = dependencyPlanner.BuildCurrentConfigurationPlan(graph);
        var conflicts = await conflictDiscoveryService
            .DiscoverAsync(destinationSpaceId, graph, ct)
            .ConfigureAwait(false);
        var previewPlan = previewPlanner.BuildPreviewPlan(dependencyPlan, conflicts);

        return new OctopusImportPlanningSnapshot(graph, dependencyPlan, conflicts, previewPlan);
    }

    private async Task<OctopusInputExtractionResult> ExtractInputAsync(string temporaryUploadPath, CancellationToken ct)
    {
        var fileInfo = new FileInfo(temporaryUploadPath);
        var cacheKey = new ExtractionCacheKey(Path.GetFullPath(temporaryUploadPath), fileInfo.Length, fileInfo.LastWriteTimeUtc);

        var extraction = _extractions.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<OctopusInputExtractionResult>>(
                () => sourceReader.ReadAsync(temporaryUploadPath, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await extraction.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!extraction.Value.IsCompleted)
        {
            throw;
        }
        catch
        {
            _extractions.TryRemove(cacheKey, out _);
            throw;
        }
    }

    // The history bundle beside the main file is part of the input: a finished REST fetch must invalidate the cache.
    private sealed record ExtractionCacheKey(string FullPath, long Length, DateTime LastWriteTimeUtc);
}
