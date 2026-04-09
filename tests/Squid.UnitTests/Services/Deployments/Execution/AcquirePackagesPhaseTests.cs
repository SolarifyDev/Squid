using System.Linq;
using Moq;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Execution;

/// <summary>
/// Integration tests for AcquirePackagesAsync — verifies both P0 (FeedId validation) and P1
/// (DeploymentPackageContext population) alongside the flat-field backward-compatible context.
/// </summary>
public class AcquirePackagesPhaseTests : IDisposable
{
    private readonly Mock<IDeploymentLifecycle> _lifecycleMock;
    private readonly Mock<IExternalFeedDataProvider> _feedProviderMock;
    private readonly Mock<IPackageAcquisitionService> _acquisitionMock;
    private readonly Mock<IActionHandlerRegistry> _handlerRegistryMock;
    private readonly Mock<IDeploymentInterruptionService> _interruptionMock;
    private readonly Mock<IDeploymentCheckpointService> _checkpointMock;
    private readonly Mock<IServerTaskService> _taskServiceMock;
    private readonly Mock<ITransportRegistry> _transportRegistryMock;
    private readonly ExecuteStepsPhase _phase;
    private readonly List<DeploymentLifecycleEvent> _capturedEvents;
    private readonly DeploymentTaskContext _ctx;

    public AcquirePackagesPhaseTests()
    {
        _capturedEvents = new List<DeploymentLifecycleEvent>();
        _lifecycleMock = new Mock<IDeploymentLifecycle>();
        _feedProviderMock = new Mock<IExternalFeedDataProvider>();
        _acquisitionMock = new Mock<IPackageAcquisitionService>();
        _handlerRegistryMock = new Mock<IActionHandlerRegistry>();
        _interruptionMock = new Mock<IDeploymentInterruptionService>();
        _checkpointMock = new Mock<IDeploymentCheckpointService>();
        _taskServiceMock = new Mock<IServerTaskService>();
        _transportRegistryMock = new Mock<ITransportRegistry>();

        _lifecycleMock.Setup(l => l.EmitAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>()))
            .Callback<DeploymentLifecycleEvent, CancellationToken>((e, _) => _capturedEvents.Add(e))
            .Returns(Task.CompletedTask);

        _phase = new ExecuteStepsPhase(
            _handlerRegistryMock.Object,
            _lifecycleMock.Object,
            _interruptionMock.Object,
            _checkpointMock.Object,
            _taskServiceMock.Object,
            _transportRegistryMock.Object,
            _feedProviderMock.Object,
            _acquisitionMock.Object);

        _ctx = new DeploymentTaskContext
        {
            ServerTaskId = 1,
            Deployment = new Deployment { Id = 1 },
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            Steps = new List<DeploymentStepDto>(),
            Variables = new List<Squid.Message.Models.Deployments.Variable.VariableDto>(),
            AcquiredPackages = new Dictionary<string, PackageAcquisitionResult>()
        };
    }

    public void Dispose() { }

    private DeploymentStepDto MakeAcquirePackagesStep() => new()
    {
        Id = 999,
        StepOrder = 1,
        Name = "Acquire Packages",
        StepType = "AcquirePackages",
        Condition = "Success",
        StartTrigger = "Start",
        PackageRequirement = "AfterPackageAcquisition"
    };

    private List<ReleaseSelectedPackage> MakePackages(params (int feedId, string pkgId, string version)[] specs)
        => specs.Select((s, i) => new ReleaseSelectedPackage
        {
            Id = i + 1,
            ReleaseId = 1,
            FeedId = s.feedId,
            ActionName = $"Action{i + 1}",
            PackageReferenceName = s.pkgId,
            Version = s.version
        }).ToList();

    private ExternalFeed MakeFeed(int id) => new() { Id = id, FeedType = "Generic", FeedUri = "https://packages.example.com" };

    private static PackageAcquisitionResult MakeResult(string packageId, string version, int size = 1024)
        => new($"/tmp/{packageId}.{version}.nupkg", packageId, version, size, "abc123hash");

    // === P0: FeedId validation ===

    [Fact]
    public async Task AcquirePackages_FeedIdZero_EmitsPackageDownloadFailedEvent()
    {
        _ctx.SelectedPackages = MakePackages((0, "nginx", "1.21.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        // 3 events: Acquiring + Failed + Acquired
        _capturedEvents.Count.ShouldBe(3);
        var failedEvent = _capturedEvents[1] as PackageDownloadFailedEvent;
        failedEvent.ShouldNotBeNull();
        failedEvent.Context.PackageError.ShouldContain("Invalid FeedId");
        failedEvent.Context.PackageError.ShouldContain("0");
        failedEvent.Context.PackageId.ShouldBe("nginx");
        failedEvent.Context.PackageVersion.ShouldBe("1.21.0");
        failedEvent.Context.PackageFeedId.ShouldBe(0);
    }

    [Fact]
    public async Task AcquirePackages_FeedIdNegative_EmitsPackageDownloadFailedEvent()
    {
        _ctx.SelectedPackages = MakePackages((-5, "redis", "7.0.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        // 3 events: Acquiring + Failed + Acquired
        _capturedEvents.Count.ShouldBe(3);
        var failedEvent = _capturedEvents[1] as PackageDownloadFailedEvent;
        failedEvent.ShouldNotBeNull();
        failedEvent.Context.PackageError.ShouldContain("Invalid FeedId");
        failedEvent.Context.PackageError.ShouldContain("-5");
        failedEvent.Context.PackageId.ShouldBe("redis");
    }

    [Fact]
    public async Task AcquirePackages_FeedIdZero_DoesNotCallFeedProvider()
    {
        _ctx.SelectedPackages = MakePackages((0, "nginx", "1.21.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _feedProviderMock.Verify(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AcquirePackages_FeedIdZero_DoesNotCallAcquisitionService()
    {
        _ctx.SelectedPackages = MakePackages((0, "nginx", "1.21.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _acquisitionMock.Verify(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AcquirePackages_MixedValidAndInvalidFeedIds_EmitsFailedForInvalid_SkipsValid()
    {
        _ctx.SelectedPackages = MakePackages(
            (0, "nginx", "1.21.0"),
            (10, "redis", "7.0.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), "redis", "7.0.0", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("redis", "7.0.0"));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        // 5 events: Acquiring + Downloading(failed for idx 0) + Downloading + Downloaded + Acquired
        _capturedEvents.Count.ShouldBe(5);

        // Index 0: failed
        var failed = _capturedEvents[1] as PackageDownloadFailedEvent;
        failed.ShouldNotBeNull();
        failed.Context.PackageIndex.ShouldBe(0);
        failed.Context.PackageError.ShouldContain("Invalid FeedId");

        // Index 2: downloading for idx 1
        var downloading = _capturedEvents[2] as PackageDownloadingEvent;
        downloading.ShouldNotBeNull();
        downloading.Context.PackageIndex.ShouldBe(1);
        downloading.Context.PackageId.ShouldBe("redis");

        // Index 3: downloaded
        var downloaded = _capturedEvents[3] as PackageDownloadedEvent;
        downloaded.ShouldNotBeNull();
        downloaded.Context.PackageIndex.ShouldBe(1);
        downloaded.Context.PackageId.ShouldBe("redis");
        downloaded.Context.PackageSizeBytes.ShouldBe(1024);
    }

    // === P1: DeploymentPackageContext population ===

    [Fact]
    public async Task AcquirePackages_Success_AllEventsHavePackagesProperty()
    {
        _ctx.SelectedPackages = MakePackages((10, "nginx", "1.21.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), "nginx", "1.21.0", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("nginx", "1.21.0", 5000));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        // 4 events: Acquiring + Downloading + Downloaded + Acquired
        _capturedEvents.Count.ShouldBe(4);

        foreach (var evt in _capturedEvents)
        {
            var ctx = (evt as dynamic).Context as DeploymentEventContext;
            ctx.ShouldNotBeNull();
            ctx.Packages.ShouldNotBeNull();
            ctx.Packages.SelectedPackages.ShouldBeSameAs(_ctx.SelectedPackages);
        }
    }

    [Fact]
    public async Task AcquirePackages_Success_PackagesAcquiringEvent_HasCorrectPackageCount()
    {
        _ctx.SelectedPackages = MakePackages(
            (10, "nginx", "1.21.0"),
            (10, "redis", "7.0.0"),
            (20, "postgres", "15.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10), MakeFeed(20) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalFeed f, string id, string v, int _, CancellationToken _) => MakeResult(id, v));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        var acquiring = _capturedEvents[0] as PackagesAcquiringEvent;
        acquiring.ShouldNotBeNull();
        acquiring.Context.PackageCount.ShouldBe(3);
        acquiring.Context.Packages.PackageCount.ShouldBe(3);
        acquiring.Context.Packages.SelectedPackages.Count.ShouldBe(3);
        acquiring.Context.Packages.PackageId.ShouldBeEmpty();
        acquiring.Context.Packages.PackageTotalSizeBytes.ShouldBe(0);
    }

    [Fact]
    public async Task AcquirePackages_Success_PackageDownloadingEvent_HasPerPackageContext()
    {
        _ctx.SelectedPackages = MakePackages((10, "nginx", "1.21.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), "nginx", "1.21.0", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("nginx", "1.21.0"));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        var downloading = _capturedEvents[1] as PackageDownloadingEvent;
        downloading.ShouldNotBeNull();
        downloading.Context.PackageIndex.ShouldBe(0);
        downloading.Context.PackageCount.ShouldBe(1);
        downloading.Context.PackageId.ShouldBe("nginx");
        downloading.Context.PackageVersion.ShouldBe("1.21.0");
        downloading.Context.PackageFeedId.ShouldBe(10);
        downloading.Context.Packages.PackageId.ShouldBe("nginx");
        downloading.Context.Packages.PackageVersion.ShouldBe("1.21.0");
        downloading.Context.Packages.PackageFeedId.ShouldBe(10);
        downloading.Context.Packages.PackageIndex.ShouldBe(0);
        downloading.Context.Packages.PackageCount.ShouldBe(1);
        downloading.Context.Packages.PackageSizeBytes.ShouldBe(0);
        downloading.Context.Packages.PackageError.ShouldBeEmpty();
    }

    [Fact]
    public async Task AcquirePackages_Success_PackageDownloadedEvent_HasSizeAndHash()
    {
        _ctx.SelectedPackages = MakePackages((10, "nginx", "1.21.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), "nginx", "1.21.0", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageAcquisitionResult("/tmp/nginx.1.21.0.nupkg", "nginx", "1.21.0", 12345, "deadbeef"));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        var downloaded = _capturedEvents[2] as PackageDownloadedEvent;
        downloaded.ShouldNotBeNull();
        downloaded.Context.PackageSizeBytes.ShouldBe(12345);
        downloaded.Context.PackageHash.ShouldBe("deadbeef");
        downloaded.Context.PackageLocalPath.ShouldBe("/tmp/nginx.1.21.0.nupkg");
        downloaded.Context.Packages.PackageSizeBytes.ShouldBe(12345);
        downloaded.Context.Packages.PackageHash.ShouldBe("deadbeef");
        downloaded.Context.Packages.PackageLocalPath.ShouldBe("/tmp/nginx.1.21.0.nupkg");
        downloaded.Context.Packages.PackageError.ShouldBeEmpty();
    }

    [Fact]
    public async Task AcquirePackages_Success_PackageDownloadedEvent_CumulativeTotalSizeBytes()
    {
        _ctx.SelectedPackages = MakePackages((10, "pkg1", "1.0.0"), (10, "pkg2", "2.0.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), "pkg1", "1.0.0", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("pkg1", "1.0.0", 100));
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), "pkg2", "2.0.0", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("pkg2", "2.0.0", 200));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        // Downloaded for pkg1 (idx 0): totalSize = 100
        var downloaded1 = _capturedEvents[2] as PackageDownloadedEvent;
        downloaded1.ShouldNotBeNull();
        downloaded1.Context.PackageIndex.ShouldBe(0);
        downloaded1.Context.Packages.PackageTotalSizeBytes.ShouldBe(100);

        // Downloaded for pkg2 (idx 1): totalSize = 300
        var downloaded2 = _capturedEvents[4] as PackageDownloadedEvent;
        downloaded2.ShouldNotBeNull();
        downloaded2.Context.PackageIndex.ShouldBe(1);
        downloaded2.Context.Packages.PackageTotalSizeBytes.ShouldBe(300);
    }

    [Fact]
    public async Task AcquirePackages_Success_PackagesAcquiredEvent_HasCumulativeTotalSize()
    {
        _ctx.SelectedPackages = MakePackages((10, "nginx", "1.21.0"), (10, "redis", "7.0.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalFeed _, string id, string v, int _, CancellationToken _) => MakeResult(id, v, 500));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        var acquired = _capturedEvents[^1] as PackagesAcquiredEvent;
        acquired.ShouldNotBeNull();
        acquired.Context.PackageTotalSizeBytes.ShouldBe(1000);
        acquired.Context.Packages.PackageTotalSizeBytes.ShouldBe(1000);
        acquired.Context.PackageCount.ShouldBe(2);
        acquired.Context.Packages.PackageCount.ShouldBe(2);
    }

    [Fact]
    public async Task AcquirePackages_FeedNotFound_EmitsFailedWithCorrectError()
    {
        _ctx.SelectedPackages = MakePackages((999, "nonexistent", "1.0.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed>());

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        var failed = _capturedEvents.OfType<PackageDownloadFailedEvent>().ShouldHaveSingleItem();
        failed.Context.PackageError.ShouldContain("Feed 999 not found");
        failed.Context.Packages.PackageError.ShouldContain("Feed 999 not found");
        failed.Context.Packages.PackageFeedId.ShouldBe(999);
    }

    [Fact]
    public async Task AcquirePackages_AcquisitionThrows_EmitsFailedEvent()
    {
        _ctx.SelectedPackages = MakePackages((10, "nginx", "1.21.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), "nginx", "1.21.0", 1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Network unreachable"));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        var failed = _capturedEvents.OfType<PackageDownloadFailedEvent>().ShouldHaveSingleItem();
        failed.Context.PackageError.ShouldBe("Network unreachable");
        failed.Context.Packages.PackageError.ShouldBe("Network unreachable");
    }

    [Fact]
    public async Task AcquirePackages_NoPackages_NoEventsEmitted()
    {
        _ctx.SelectedPackages = new List<ReleaseSelectedPackage>();
        _ctx.Steps = [MakeAcquirePackagesStep()];

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturedEvents.ShouldBeEmpty();
        _acquisitionMock.Verify(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AcquirePackages_AllFailed_StillEmitsPackagesAcquiredEvent()
    {
        _ctx.SelectedPackages = MakePackages((0, "pkg1", "1.0.0"), (999, "pkg2", "2.0.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed>());

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        var acquired = _capturedEvents.OfType<PackagesAcquiredEvent>().ShouldHaveSingleItem();
        acquired.Context.PackageTotalSizeBytes.ShouldBe(0);
        acquired.Context.Packages.PackageTotalSizeBytes.ShouldBe(0);
    }

    [Fact]
    public async Task AcquirePackages_Success_AcquiredPackagesStoredInContext()
    {
        _ctx.SelectedPackages = MakePackages((10, "nginx", "1.21.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), "nginx", "1.21.0", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResult("nginx", "1.21.0"));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _ctx.AcquiredPackages.ShouldContainKey("nginx");
        _ctx.AcquiredPackages["nginx"].Version.ShouldBe("1.21.0");
    }

    [Fact]
    public async Task AcquirePackages_InvalidFeedId_AcquiredPackagesNotStored()
    {
        _ctx.SelectedPackages = MakePackages((0, "nginx", "1.21.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _ctx.AcquiredPackages.ShouldBeEmpty();
    }

    // === Backward compatibility: flat fields are populated alongside Packages property ===

    [Fact]
    public async Task AcquirePackages_Success_FlatFieldsAndPackagesPropertyMatch()
    {
        _ctx.SelectedPackages = MakePackages((10, "nginx", "1.21.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), "nginx", "1.21.0", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageAcquisitionResult("/tmp/a", "nginx", "1.21.0", 1234, "xyz"));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        var downloading = _capturedEvents[1] as PackageDownloadingEvent;
        downloading.ShouldNotBeNull();

        // Flat field matches Packages property
        downloading.Context.PackageId.ShouldBe(downloading.Context.Packages.PackageId);
        downloading.Context.PackageVersion.ShouldBe(downloading.Context.Packages.PackageVersion);
        downloading.Context.PackageFeedId.ShouldBe(downloading.Context.Packages.PackageFeedId);
        downloading.Context.PackageIndex.ShouldBe(downloading.Context.Packages.PackageIndex);
        downloading.Context.PackageCount.ShouldBe(downloading.Context.Packages.PackageCount);

        var downloaded = _capturedEvents[2] as PackageDownloadedEvent;
        downloaded.ShouldNotBeNull();
        downloaded.Context.PackageId.ShouldBe(downloaded.Context.Packages.PackageId);
        downloaded.Context.PackageSizeBytes.ShouldBe(downloaded.Context.Packages.PackageSizeBytes);
        downloaded.Context.PackageHash.ShouldBe(downloaded.Context.Packages.PackageHash);
        downloaded.Context.PackageLocalPath.ShouldBe(downloaded.Context.Packages.PackageLocalPath);
    }

    [Fact]
    public async Task AcquirePackages_Success_AcquiringEvent_SelectedPackagesMatchesPackagesProperty()
    {
        _ctx.SelectedPackages = MakePackages((10, "nginx", "1.21.0"), (10, "redis", "7.0.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalFeed _, string id, string v, int _, CancellationToken _) => MakeResult(id, v));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        var acquiring = _capturedEvents[0] as PackagesAcquiringEvent;
        acquiring.ShouldNotBeNull();

        // Backward-compatible flat field
        acquiring.Context.SelectedPackages.ShouldBeSameAs(_ctx.SelectedPackages);
        // P1 nested context
        acquiring.Context.Packages.SelectedPackages.ShouldBeSameAs(_ctx.SelectedPackages);
        acquiring.Context.SelectedPackages.ShouldBeSameAs(acquiring.Context.Packages.SelectedPackages);
    }

    // === Multiple feeds ===

    [Fact]
    public async Task AcquirePackages_MultipleFeeds_FeedIdsDistinct()
    {
        _ctx.SelectedPackages = MakePackages(
            (10, "nginx", "1.21.0"),
            (20, "redis", "7.0.0"),
            (10, "prometheus", "2.40.0"));
        _ctx.Steps = [MakeAcquirePackagesStep()];

        _feedProviderMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.Is<List<int>>(ids => ids.Count == 2 && ids.Contains(10) && ids.Contains(20)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExternalFeed> { MakeFeed(10), MakeFeed(20) });
        _acquisitionMock.Setup(a => a.AcquireAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalFeed f, string id, string v, int _, CancellationToken _) => MakeResult(id, v));

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        // 8 events: Acquiring + (Downloading + Downloaded) * 3 + Acquired
        _capturedEvents.Count.ShouldBe(8);

        var downloadingEvents = _capturedEvents.OfType<PackageDownloadingEvent>().ToList();
        downloadingEvents.Count.ShouldBe(3);
        downloadingEvents[0].Context.PackageFeedId.ShouldBe(10);
        downloadingEvents[1].Context.PackageFeedId.ShouldBe(20);
        downloadingEvents[2].Context.PackageFeedId.ShouldBe(10);
    }
}
