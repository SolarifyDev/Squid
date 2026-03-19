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
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.UnitTests.Services.Deployments.ReleaseServices;

public class ReleaseServiceCreateReleaseExceptionTests
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
    public async Task CreateReleaseAsync_ProjectNotFound_ThrowsReleaseProjectNotFoundException()
    {
        var sut = CreateSut();
        var command = ValidCommand();

        _projectDataProvider.Setup(x => x.GetProjectByIdAsync(command.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Project)null);

        var ex = await Should.ThrowAsync<ReleaseProjectNotFoundException>(
            () => sut.CreateReleaseAsync(command, CancellationToken.None));

        ex.ProjectId.ShouldBe(command.ProjectId);
    }

    [Fact]
    public async Task CreateReleaseAsync_ChannelNotFound_ThrowsReleaseChannelNotFoundException()
    {
        var sut = CreateSut();
        var command = ValidCommand();

        _projectDataProvider.Setup(x => x.GetProjectByIdAsync(command.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = command.ProjectId, SpaceId = 1 });

        _channelDataProvider.Setup(x => x.GetChannelByIdAsync(command.ChannelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Channel)null);

        var ex = await Should.ThrowAsync<ReleaseChannelNotFoundException>(
            () => sut.CreateReleaseAsync(command, CancellationToken.None));

        ex.ChannelId.ShouldBe(command.ChannelId);
    }

    [Fact]
    public async Task CreateReleaseAsync_ChannelProjectMismatch_ThrowsReleaseChannelProjectMismatchException()
    {
        var sut = CreateSut();
        var command = ValidCommand();

        _projectDataProvider.Setup(x => x.GetProjectByIdAsync(command.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = command.ProjectId, SpaceId = 1 });

        _channelDataProvider.Setup(x => x.GetChannelByIdAsync(command.ChannelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Channel { Id = command.ChannelId, ProjectId = 999, SpaceId = 1 });

        var ex = await Should.ThrowAsync<ReleaseChannelProjectMismatchException>(
            () => sut.CreateReleaseAsync(command, CancellationToken.None));

        ex.ChannelId.ShouldBe(command.ChannelId);
        ex.ExpectedProjectId.ShouldBe(command.ProjectId);
        ex.ActualProjectId.ShouldBe(999);
    }

    [Fact]
    public async Task CreateReleaseAsync_SpaceMismatch_ThrowsReleaseSpaceMismatchException()
    {
        var sut = CreateSut();
        var command = ValidCommand();

        _projectDataProvider.Setup(x => x.GetProjectByIdAsync(command.ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = command.ProjectId, SpaceId = 1 });

        _channelDataProvider.Setup(x => x.GetChannelByIdAsync(command.ChannelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Channel { Id = command.ChannelId, ProjectId = command.ProjectId, SpaceId = 2 });

        var ex = await Should.ThrowAsync<ReleaseSpaceMismatchException>(
            () => sut.CreateReleaseAsync(command, CancellationToken.None));

        ex.ProjectId.ShouldBe(command.ProjectId);
        ex.ChannelId.ShouldBe(command.ChannelId);
        ex.ProjectSpaceId.ShouldBe(1);
        ex.ChannelSpaceId.ShouldBe(2);
    }

    private static CreateReleaseCommand ValidCommand() => new()
    {
        Version = "1.0.0",
        ProjectId = 1,
        ChannelId = 2
    };
}
