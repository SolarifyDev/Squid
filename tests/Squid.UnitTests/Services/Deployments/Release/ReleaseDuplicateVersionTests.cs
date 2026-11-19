using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.Releases.Exceptions;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Models.Deployments.Snapshots;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.UnitTests.Services.Deployments.ReleaseServices;

public class ReleaseDuplicateVersionTests
{
    private readonly Mock<IMapper> _mapper = new();
    private readonly Mock<IReleaseDataProvider> _releaseDataProvider = new();
    private readonly Mock<IReleaseSelectedPackageDataProvider> _releaseSelectedPackageDataProvider = new();
    private readonly Mock<IDeploymentCompletionDataProvider> _deploymentCompletionDataProvider = new();
    private readonly Mock<IDeploymentSnapshotService> _deploymentSnapshotService = new();
    private readonly Mock<IProjectDataProvider> _projectDataProvider = new();
    private readonly Mock<IChannelDataProvider> _channelDataProvider = new();
    private readonly Mock<IChannelVersionRuleDataProvider> _channelVersionRuleDataProvider = new();
    private readonly Mock<IChannelVersionRuleValidator> _channelVersionRuleValidator = new();
    private readonly Mock<ILifecycleResolver> _lifecycleResolver = new();
    private readonly Mock<ILifecycleProgressionEvaluator> _progressionEvaluator = new();
    private readonly Mock<ILifeCycleDataProvider> _lifeCycleDataProvider = new();
    private readonly Mock<IEnvironmentDataProvider> _environmentDataProvider = new();
    private readonly Mock<IRepository> _repository = new();

    private ReleaseService CreateSut()
    {
        _mapper.Setup(x => x.Map<ReleaseEntity>(It.IsAny<CreateReleaseCommand>()))
            .Returns(new ReleaseEntity());

        return new ReleaseService(
            _mapper.Object,
            _releaseDataProvider.Object,
            _releaseSelectedPackageDataProvider.Object,
            _deploymentCompletionDataProvider.Object,
            _deploymentSnapshotService.Object,
            _projectDataProvider.Object,
            _channelDataProvider.Object,
            _channelVersionRuleDataProvider.Object,
            _channelVersionRuleValidator.Object,
            _lifecycleResolver.Object,
            _progressionEvaluator.Object,
            _lifeCycleDataProvider.Object,
            _environmentDataProvider.Object,
            _repository.Object);
    }

    [Fact]
    public async Task CreateReleaseAsync_DuplicateVersion_ThrowsReleaseDuplicateVersionException()
    {
        var sut = CreateSut();
        var command = new CreateReleaseCommand { Version = "1.0.0", ProjectId = 1, ChannelId = 2 };

        SetupValidProjectAndChannel(command);

        _releaseDataProvider.Setup(x => x.GetReleaseByVersionAsync(command.ProjectId, command.ChannelId, command.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReleaseEntity { Id = 99, Version = "1.0.0", ProjectId = 1, ChannelId = 2 });

        var ex = await Should.ThrowAsync<ReleaseDuplicateVersionException>(
            () => sut.CreateReleaseAsync(command, CancellationToken.None));

        ex.ProjectId.ShouldBe(command.ProjectId);
        ex.ChannelId.ShouldBe(command.ChannelId);
        ex.Version.ShouldBe(command.Version);
    }

    [Fact]
    public async Task CreateReleaseAsync_SameVersionDifferentChannel_DoesNotThrow()
    {
        var sut = CreateSut();
        var command = new CreateReleaseCommand { Version = "1.0.0", ProjectId = 1, ChannelId = 2 };

        SetupValidProjectAndChannel(command);

        _releaseDataProvider.Setup(x => x.GetReleaseByVersionAsync(command.ProjectId, command.ChannelId, command.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReleaseEntity)null);

        _deploymentSnapshotService.Setup(x => x.SnapshotVariableSetFromReleaseAsync(It.IsAny<ReleaseEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VariableSetSnapshotDto { Id = 1 });
        _deploymentSnapshotService.Setup(x => x.SnapshotProcessFromReleaseAsync(It.IsAny<ReleaseEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentProcessSnapshotDto { Id = 1 });
        _mapper.Setup(x => x.Map<Squid.Message.Models.Deployments.Release.ReleaseDto>(It.IsAny<ReleaseEntity>()))
            .Returns(new Squid.Message.Models.Deployments.Release.ReleaseDto());

        await Should.NotThrowAsync(() => sut.CreateReleaseAsync(command, CancellationToken.None));
    }

    // The Octopus history import creates thousands of releases of one project in one confirmation; the project's configuration does not
    // change between them, so it hands the first one's snapshots to the rest instead of rebuilding (and re-hashing) the same snapshot.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CreateReleaseAsync_HistoricalSnapshotsGiven_ReusedOnlyForAHistoricalImport(bool isHistoricalImport, bool expectSnapshotTaken)
    {
        var sut = CreateSut();
        var command = new CreateReleaseCommand { Version = "1.0.0", ProjectId = 1, ChannelId = 2, IsHistoricalImport = isHistoricalImport, HistoricalSnapshots = new HistoricalReleaseSnapshots(41, 42) };
        ReleaseEntity created = null;

        SetupValidProjectAndChannel(command);
        _deploymentSnapshotService.Setup(x => x.SnapshotVariableSetFromReleaseAsync(It.IsAny<ReleaseEntity>(), It.IsAny<CancellationToken>())).ReturnsAsync(new VariableSetSnapshotDto { Id = 7 });
        _deploymentSnapshotService.Setup(x => x.SnapshotProcessFromReleaseAsync(It.IsAny<ReleaseEntity>(), It.IsAny<CancellationToken>())).ReturnsAsync(new DeploymentProcessSnapshotDto { Id = 8 });
        _releaseDataProvider.Setup(x => x.CreateReleaseAsync(It.IsAny<ReleaseEntity>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Callback((ReleaseEntity release, bool _, CancellationToken _) => created = release);
        _mapper.Setup(x => x.Map<Squid.Message.Models.Deployments.Release.ReleaseDto>(It.IsAny<ReleaseEntity>())).Returns(new Squid.Message.Models.Deployments.Release.ReleaseDto());

        await sut.CreateReleaseAsync(command, CancellationToken.None);

        (created.ProjectVariableSetSnapshotId, created.ProjectDeploymentProcessSnapshotId).ShouldBe(expectSnapshotTaken ? (7, 8) : (41, 42));
        _deploymentSnapshotService.Verify(x => x.SnapshotVariableSetFromReleaseAsync(It.IsAny<ReleaseEntity>(), It.IsAny<CancellationToken>()), expectSnapshotTaken ? Times.Once() : Times.Never());
        _deploymentSnapshotService.Verify(x => x.SnapshotProcessFromReleaseAsync(It.IsAny<ReleaseEntity>(), It.IsAny<CancellationToken>()), expectSnapshotTaken ? Times.Once() : Times.Never());
    }

    // Never bound from the API: a client cannot point its release at another project's snapshots.
    [Fact]
    public void CreateReleaseCommand_HistoricalSnapshots_NeverBoundFromJson()
    {
        var command = System.Text.Json.JsonSerializer.Deserialize<CreateReleaseCommand>("""{"Version":"1.0.0","HistoricalSnapshots":{"VariableSetSnapshotId":1,"DeploymentProcessSnapshotId":2},"IsHistoricalImport":true}""");

        command.HistoricalSnapshots.ShouldBeNull();
        command.IsHistoricalImport.ShouldBeFalse();
    }

    private void SetupValidProjectAndChannel(CreateReleaseCommand command)
    {
        _projectDataProvider.Setup(x => x.GetProjectByIdAsync(command.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = command.ProjectId, SpaceId = 1 });

        _channelDataProvider.Setup(x => x.GetChannelByIdAsync(command.ChannelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Channel { Id = command.ChannelId, ProjectId = command.ProjectId, SpaceId = 1 });
    }
}
