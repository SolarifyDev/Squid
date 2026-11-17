using System.IO;
using System.Linq;
using Squid.Core.DependencyInjection;
using Squid.Core.Handlers.CommandHandlers.OctopusImport;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// The parsed source outlives a request: extract, project picker, preview, every validate and confirm each run in their own
/// container scope, and a history bundle of a large space must not be re-read and re-parsed on each of them.
/// </summary>
public sealed class OctopusImportExtractionCacheTests : IDisposable
{
    private readonly List<string> _paths = [];
    private readonly ManualTime _time = new();
    private readonly OctopusImportExtractionCache _cache;

    public OctopusImportExtractionCacheTests() => _cache = new OctopusImportExtractionCache(_time);

    // The container builds the cache once per process; scoped, it would be empty on every request again.
    [Fact]
    public void Cache_IsAProcessWideSingleton()
        => typeof(ISingletonDependency).IsAssignableFrom(typeof(IOctopusImportExtractionCache)).ShouldBeTrue();

    [Fact]
    public async Task SeparateRequests_ShareOneParse()
    {
        var path = TempFile();
        var reads = 0;

        for (var request = 0; request < 3; request++)
            await _cache.GetOrReadAsync(path, () => { reads++; return Task.FromResult(Empty()); });

        reads.ShouldBe(1);
    }

    // Each request builds its own pipeline (a scoped service); they share the container's cache.
    [Fact]
    public async Task PipelinesOfSeparateRequests_ExtractTheSourceOnce()
    {
        var path = TempFile();
        var extractor = new Mock<IOctopusInputExtractor>();
        extractor.Setup(e => e.ExtractStandaloneJsonAsync(It.IsAny<Stream>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(Empty());

        await Pipeline(extractor).BuildGraphAsync(path);
        await Pipeline(extractor).BuildGraphAsync(path);
        await Pipeline(extractor).BuildPreviewAsync(path, 7);

        extractor.Verify(e => e.ExtractStandaloneJsonAsync(It.IsAny<Stream>(), Path.GetFileName(path), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The extract step's parse is the one the project picker and preview then plan from.
    [Fact]
    public async Task ExtractStep_ThenPlanning_ParsesTheSourceOnce()
    {
        var path = TempFile();
        var extractor = new Mock<IOctopusInputExtractor>();
        extractor.Setup(e => e.ExtractStandaloneJsonAsync(It.IsAny<Stream>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(Empty());
        var reader = new OctopusImportSourceReader(new Mock<IOctopusArchiveExtractor>().Object, extractor.Object);
        var sessions = new Mock<IOctopusImportSessionDataProvider>();
        var sessionId = Guid.NewGuid();
        sessions.Setup(p => p.GetSessionAsync(sessionId, 42, 7, It.IsAny<CancellationToken>())).ReturnsAsync(new Squid.Core.Persistence.Entities.Deployments.OctopusImportSession { SessionId = sessionId, OwnerUserId = 42, DestinationSpaceId = 7, State = OctopusImportSessionState.Uploaded.ToString(), TemporaryUploadPath = path, SourceSummaryJson = "{}" });
        var user = new Mock<Squid.Core.Services.Identity.ICurrentUser>();
        user.SetupGet(u => u.Id).Returns(42);
        var inventory = new Mock<IOctopusManifestInventoryBuilder>();
        inventory.Setup(b => b.Build(It.IsAny<OctopusInputExtractionResult>())).Returns(new OctopusManifestInventoryResult(new OctopusExportManifestDto(), [], [], []));
        var graph = new Mock<IOctopusResourceGraphBuilder>();
        graph.Setup(b => b.Build(It.IsAny<OctopusManifestInventoryResult>())).Returns(new OctopusResourceGraph([], [], [], []));
        var handler = new ExtractOctopusImportCommandHandler(sessions.Object, Mock.Of<IOctopusImportSessionService>(), user.Object, reader, inventory.Object, graph.Object, _cache);
        var context = new Mock<IReceiveContext<ExtractOctopusImportCommand>>();
        context.SetupGet(c => c.Message).Returns(new ExtractOctopusImportCommand { SessionId = sessionId, SpaceId = 7 });

        await handler.Handle(context.Object, CancellationToken.None);
        await Pipeline(extractor).BuildGraphAsync(path);

        extractor.Verify(e => e.ExtractStandaloneJsonAsync(It.IsAny<Stream>(), Path.GetFileName(path), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RewrittenFile_IsReadAgain()
    {
        var path = TempFile();
        var reads = 0;

        await _cache.GetOrReadAsync(path, () => { reads++; return Task.FromResult(Empty()); });
        File.WriteAllText(path, """{"changed":true}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        await _cache.GetOrReadAsync(path, () => { reads++; return Task.FromResult(Empty()); });

        reads.ShouldBe(2);
    }

    [Fact]
    public async Task FailedRead_IsNotKept()
    {
        var path = TempFile();
        var reads = 0;

        await Should.ThrowAsync<InvalidOperationException>(() => _cache.GetOrReadAsync(path, () => { reads++; return Task.FromException<OctopusInputExtractionResult>(new InvalidOperationException()); }));
        await _cache.GetOrReadAsync(path, () => { reads++; return Task.FromResult(Empty()); });

        reads.ShouldBe(2);
    }

    // A caller that gives up (a request aborted) does not cancel the read the next caller waits on.
    [Fact]
    public async Task CallerGivesUp_TheReadCompletesForTheNextCaller()
    {
        var path = TempFile();
        var gate = new TaskCompletionSource<OctopusInputExtractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var aborted = new CancellationTokenSource();

        var first = _cache.GetOrReadAsync(path, () => { reads++; return gate.Task; }, aborted.Token);
        await aborted.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => first);

        gate.SetResult(Empty());

        (await _cache.GetOrReadAsync(path, () => { reads++; return Task.FromResult<OctopusInputExtractionResult>(null); })).ShouldNotBeNull();
        reads.ShouldBe(1);
    }

    // Memory is bounded: past the capacity the least recently used source goes, and an idle one is given back.
    [Fact]
    public async Task BeyondCapacity_LeastRecentlyUsedSourceIsDropped()
    {
        var paths = Enumerable.Range(0, OctopusImportExtractionCache.Capacity + 1).Select(_ => TempFile()).ToList();
        var reads = new Dictionary<string, int>();

        async Task Read(string path)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await _cache.GetOrReadAsync(path, () => { reads[path] = reads.GetValueOrDefault(path) + 1; return Task.FromResult(Empty()); });
        }

        foreach (var path in paths) await Read(path);
        await Read(paths[^1]);
        await Read(paths[0]);

        _cache.Count.ShouldBe(OctopusImportExtractionCache.Capacity);
        reads[paths[0]].ShouldBe(2, "the oldest source was dropped when a new one came in");
        reads[paths[^1]].ShouldBe(1);
    }

    [Fact]
    public async Task IdleSource_IsGivenBackBySweep_UsedSourceStays()
    {
        var idle = TempFile();
        var used = TempFile();

        await _cache.GetOrReadAsync(idle, () => Task.FromResult(Empty()));
        await _cache.GetOrReadAsync(used, () => Task.FromResult(Empty()));
        _time.Advance(OctopusImportExtractionCache.IdleExpiry - TimeSpan.FromSeconds(1));
        await _cache.GetOrReadAsync(used, () => Task.FromResult(Empty()));
        _time.Advance(TimeSpan.FromSeconds(1));

        _cache.Sweep();

        _cache.Count.ShouldBe(1);
    }

    public void Dispose()
    {
        _cache.Dispose();

        foreach (var path in _paths) File.Delete(path);
    }

    private OctopusImportPlanningPipeline Pipeline(Mock<IOctopusInputExtractor> extractor)
    {
        var inventory = new Mock<IOctopusManifestInventoryBuilder>();
        inventory.Setup(b => b.Build(It.IsAny<OctopusInputExtractionResult>())).Returns(new OctopusManifestInventoryResult(null, [], [], []));
        var graph = new Mock<IOctopusResourceGraphBuilder>();
        graph.Setup(b => b.Build(It.IsAny<OctopusManifestInventoryResult>())).Returns(new OctopusResourceGraph([], [], [], []));
        var planner = new Mock<IOctopusImportDependencyPlanner>();
        planner.Setup(p => p.BuildCurrentConfigurationPlan(It.IsAny<OctopusResourceGraph>(), It.IsAny<IReadOnlyCollection<string>>())).Returns(new OctopusImportDependencyPlan([], [], [], []));
        var conflicts = new Mock<IOctopusImportConflictDiscoveryService>();
        conflicts.Setup(s => s.DiscoverAsync(It.IsAny<int>(), It.IsAny<OctopusResourceGraph>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportConflictDiscoveryResult([]));
        var preview = new Mock<IOctopusImportPreviewPlanner>();
        preview.Setup(p => p.BuildPreviewPlan(It.IsAny<OctopusImportDependencyPlan>(), It.IsAny<OctopusImportConflictDiscoveryResult>(), It.IsAny<OctopusImportChoiceSelections>())).Returns(new OctopusImportPreviewPlanDto());

        return new OctopusImportPlanningPipeline(new OctopusImportSourceReader(new Mock<IOctopusArchiveExtractor>().Object, extractor.Object), inventory.Object, graph.Object, planner.Object, conflicts.Object, preview.Object, _cache);
    }

    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        _paths.Add(path);
        return path;
    }

    private static OctopusInputExtractionResult Empty() => new([], []);

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
