using System.IO;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportPlanningPipelineTests
{
    [Fact]
    public async Task BuildPreviewAsync_WhenSameFileIsPlannedTwice_ExtractsInputOnce()
    {
        var path = CreateTempFile();
        var harness = new Harness();

        try
        {
            await harness.Pipeline.BuildPreviewAsync(path, 7);
            await harness.Pipeline.BuildPreviewAsync(path, 7);

            harness.InputExtractor.Verify(
                e => e.ExtractStandaloneJsonAsync(
                    It.IsAny<Stream>(),
                    Path.GetFileName(path),
                    null,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildPreviewAsync_WhenFileIdentityChanges_ExtractsAgain()
    {
        var path = CreateTempFile();
        var harness = new Harness();

        try
        {
            await harness.Pipeline.BuildPreviewAsync(path, 7);
            File.WriteAllText(path, """{"changed":true}""");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

            await harness.Pipeline.BuildPreviewAsync(path, 7);

            harness.InputExtractor.Verify(
                e => e.ExtractStandaloneJsonAsync(
                    It.IsAny<Stream>(),
                    Path.GetFileName(path),
                    null,
                    It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildPreviewAsync_WhenCallsOverlap_ExtractsInputOnce()
    {
        var path = CreateTempFile();
        var extractionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractionResult = new TaskCompletionSource<OctopusInputExtractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness();

        harness.InputExtractor
            .Setup(e => e.ExtractStandaloneJsonAsync(
                It.IsAny<Stream>(),
                Path.GetFileName(path),
                null,
                It.IsAny<CancellationToken>()))
            .Returns((Stream _, string _, OctopusArchiveExtractionOptions _, CancellationToken _) =>
            {
                extractionStarted.TrySetResult();
                return extractionResult.Task;
            });

        try
        {
            var first = harness.Pipeline.BuildPreviewAsync(path, 7);
            await extractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = harness.Pipeline.BuildPreviewAsync(path, 7);

            extractionResult.SetResult(ExtractionResult());
            await Task.WhenAll(first, second);

            harness.InputExtractor.Verify(
                e => e.ExtractStandaloneJsonAsync(
                    It.IsAny<Stream>(),
                    Path.GetFileName(path),
                    null,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildPreviewAsync_WhenExtractionFails_DoesNotCacheTheFailure()
    {
        var path = CreateTempFile();
        var harness = new Harness();
        var attempts = 0;

        harness.InputExtractor
            .Setup(e => e.ExtractStandaloneJsonAsync(
                It.IsAny<Stream>(),
                Path.GetFileName(path),
                null,
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException<OctopusInputExtractionResult>(new InvalidOperationException("first attempt failed"))
                    : Task.FromResult(ExtractionResult());
            });

        try
        {
            await Should.ThrowAsync<InvalidOperationException>(() => harness.Pipeline.BuildPreviewAsync(path, 7));
            await harness.Pipeline.BuildPreviewAsync(path, 7);

            attempts.ShouldBe(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        return path;
    }

    private static OctopusInputExtractionResult ExtractionResult()
        => new([], []);

    private sealed class Harness
    {
        public Harness()
        {
            InputExtractor
                .Setup(e => e.ExtractStandaloneJsonAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    null,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(ExtractionResult());
            InventoryBuilder
                .Setup(b => b.Build(It.IsAny<OctopusInputExtractionResult>()))
                .Returns(new OctopusManifestInventoryResult(null, [], [], []));
            GraphBuilder
                .Setup(b => b.Build(It.IsAny<OctopusManifestInventoryResult>()))
                .Returns(new OctopusResourceGraph([], [], [], []));
            DependencyPlanner
                .Setup(p => p.BuildCurrentConfigurationPlan(It.IsAny<OctopusResourceGraph>()))
                .Returns(new OctopusImportDependencyPlan([], [], [], []));
            ConflictDiscoveryService
                .Setup(s => s.DiscoverAsync(It.IsAny<int>(), It.IsAny<OctopusResourceGraph>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OctopusImportConflictDiscoveryResult([]));
            PreviewPlanner
                .Setup(p => p.BuildPreviewPlan(It.IsAny<OctopusImportDependencyPlan>(), It.IsAny<OctopusImportConflictDiscoveryResult>()))
                .Returns(new OctopusImportPreviewPlanDto());

            Pipeline = new OctopusImportPlanningPipeline(
                ArchiveExtractor.Object,
                InputExtractor.Object,
                InventoryBuilder.Object,
                GraphBuilder.Object,
                DependencyPlanner.Object,
                ConflictDiscoveryService.Object,
                PreviewPlanner.Object);
        }

        public Mock<IOctopusArchiveExtractor> ArchiveExtractor { get; } = new();

        public Mock<IOctopusInputExtractor> InputExtractor { get; } = new();

        public Mock<IOctopusManifestInventoryBuilder> InventoryBuilder { get; } = new();

        public Mock<IOctopusResourceGraphBuilder> GraphBuilder { get; } = new();

        public Mock<IOctopusImportDependencyPlanner> DependencyPlanner { get; } = new();

        public Mock<IOctopusImportConflictDiscoveryService> ConflictDiscoveryService { get; } = new();

        public Mock<IOctopusImportPreviewPlanner> PreviewPlanner { get; } = new();

        public OctopusImportPlanningPipeline Pipeline { get; }
    }
}
