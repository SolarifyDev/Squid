using System.Collections.Concurrent;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportPlanningPipeline : IScopedDependency
{
    /// <param name="selections">What the user selected among the resources the plan offers to decide on; null means every server default.</param>
    /// <param name="chosenProjectIds">The session's project selection, read from the session and never from a request; null plans the whole space.</param>
    Task<OctopusImportPlanningSnapshot> BuildPreviewAsync(string temporaryUploadPath, int destinationSpaceId, OctopusImportChoiceSelections selections = null, IReadOnlyCollection<string> chosenProjectIds = null, CancellationToken ct = default);

    /// <summary>The source's resource graph (the extraction is cached as for a preview): what a session's project picker lists.</summary>
    Task<OctopusResourceGraph> BuildGraphAsync(string temporaryUploadPath, CancellationToken ct = default);
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

    public async Task<OctopusImportPlanningSnapshot> BuildPreviewAsync(string temporaryUploadPath, int destinationSpaceId, OctopusImportChoiceSelections selections = null, IReadOnlyCollection<string> chosenProjectIds = null, CancellationToken ct = default)
    {
        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        var graph = await BuildGraphAsync(temporaryUploadPath, ct).ConfigureAwait(false);
        var dependencyPlan = dependencyPlanner.BuildCurrentConfigurationPlan(graph, chosenProjectIds);
        var conflicts = await conflictDiscoveryService
            .DiscoverAsync(destinationSpaceId, graph, ct)
            .ConfigureAwait(false);
        var previewPlan = previewPlanner.BuildPreviewPlan(dependencyPlan, conflicts, selections);

        return new OctopusImportPlanningSnapshot(graph, dependencyPlan, conflicts, previewPlan);
    }

    public async Task<OctopusResourceGraph> BuildGraphAsync(string temporaryUploadPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(temporaryUploadPath))
            throw new ArgumentException("Temporary upload path is required.", nameof(temporaryUploadPath));

        var extraction = await ExtractInputAsync(temporaryUploadPath, ct).ConfigureAwait(false);

        return graphBuilder.Build(inventoryBuilder.Build(extraction));
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
