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
    IOctopusArchiveExtractor archiveExtractor,
    IOctopusInputExtractor inputExtractor,
    IOctopusManifestInventoryBuilder inventoryBuilder,
    IOctopusResourceGraphBuilder graphBuilder,
    IOctopusImportDependencyPlanner dependencyPlanner,
    IOctopusImportConflictDiscoveryService conflictDiscoveryService,
    IOctopusImportPreviewPlanner previewPlanner)
    : IOctopusImportPlanningPipeline
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

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
        await using var stream = new FileStream(temporaryUploadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        if (await LooksLikeZipAsync(stream, ct).ConfigureAwait(false))
        {
            var archive = await archiveExtractor.ExtractZipAsync(stream, ct: ct).ConfigureAwait(false);
            return await inputExtractor.ExtractJsonEntriesAsync(archive.Entries, ct).ConfigureAwait(false);
        }

        return await inputExtractor
            .ExtractStandaloneJsonAsync(stream, Path.GetFileName(temporaryUploadPath), ct: ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> LooksLikeZipAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[ZipMagic.Length];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct).ConfigureAwait(false);
        stream.Position = 0;

        if (read < ZipMagic.Length)
            return false;

        for (var i = 0; i < ZipMagic.Length; i++)
        {
            if (header[i] != ZipMagic[i])
                return false;
        }

        return true;
    }
}
