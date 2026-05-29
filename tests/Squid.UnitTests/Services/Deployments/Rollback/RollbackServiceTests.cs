using System.Linq;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Rollback;
using Squid.Core.Services.Deployments.Rollback.Exceptions;
using Squid.Message.Commands.Deployments.Deployment;
using Squid.Message.Events.Deployments.Deployment;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.UnitTests.Services.Deployments.Rollback;

/// <summary>
/// PR-13 — unit tests for <see cref="RollbackService"/>. Mocks the journal
/// provider + <see cref="IDeploymentService"/> so the target resolution /
/// validation rule and the delegation to the standard deploy path are pinned
/// in isolation (no DB, no pipeline).
/// </summary>
public class RollbackServiceTests
{
    private const int ProjectId = 7;
    private const int EnvironmentId = 3;

    private readonly Mock<IDeploymentCompletionDataProvider> _journal = new();
    private readonly Mock<IDeploymentService> _deploymentService = new();
    private readonly RollbackService _sut;

    public RollbackServiceTests()
    {
        _sut = new RollbackService(_journal.Object, _deploymentService.Object);
    }

    [Fact]
    public async Task RollbackDeploymentAsync_NoReleaseId_DeploysPreviousDistinctRelease()
    {
        GivenHistory(Entry(3, 300), Entry(2, 200), Entry(1, 100));
        var captured = CaptureDelegatedCreate();

        await _sut.RollbackDeploymentAsync(Command(releaseId: null));

        captured().ShouldNotBeNull();
        captured().ReleaseId.ShouldBe(2, customMessage: "Auto rollback targets the release running before the current one.");
        captured().EnvironmentId.ShouldBe(EnvironmentId);
    }

    [Fact]
    public async Task RollbackDeploymentAsync_ExplicitValidReleaseId_DeploysThatRelease()
    {
        GivenHistory(Entry(3, 300), Entry(2, 200), Entry(1, 100));
        var captured = CaptureDelegatedCreate();

        await _sut.RollbackDeploymentAsync(Command(releaseId: 1));

        captured().ReleaseId.ShouldBe(1, customMessage: "Operator-specified prior release MUST be honoured.");
    }

    [Fact]
    public async Task RollbackDeploymentAsync_PropagatesEnvironmentAndRedeployFlags()
    {
        GivenHistory(Entry(2, 200), Entry(1, 100));
        var captured = CaptureDelegatedCreate();

        await _sut.RollbackDeploymentAsync(new RollbackDeploymentCommand
        {
            ProjectId = ProjectId,
            EnvironmentId = EnvironmentId,
            ForcePackageRedeployment = true,
            SkipActionIds = new List<int> { 42 }
        });

        captured().ForcePackageRedeployment.ShouldBeTrue();
        captured().SkipActionIds.ShouldContain(42);
    }

    [Fact]
    public async Task RollbackDeploymentAsync_ExplicitCurrentReleaseId_ThrowsAndDoesNotDeploy()
    {
        GivenHistory(Entry(3, 300), Entry(2, 200), Entry(1, 100));

        await Should.ThrowAsync<RollbackNotAvailableException>(() => _sut.RollbackDeploymentAsync(Command(releaseId: 3)));

        VerifyNoDeployment();
    }

    [Fact]
    public async Task RollbackDeploymentAsync_ExplicitUnknownReleaseId_ThrowsAndDoesNotDeploy()
    {
        GivenHistory(Entry(3, 300), Entry(2, 200), Entry(1, 100));

        await Should.ThrowAsync<RollbackNotAvailableException>(() => _sut.RollbackDeploymentAsync(Command(releaseId: 99)));

        VerifyNoDeployment();
    }

    [Fact]
    public async Task RollbackDeploymentAsync_NoSuccessfulHistory_ThrowsAndDoesNotDeploy()
    {
        GivenHistory();

        await Should.ThrowAsync<RollbackNotAvailableException>(() => _sut.RollbackDeploymentAsync(Command(releaseId: null)));

        VerifyNoDeployment();
    }

    [Fact]
    public async Task RollbackDeploymentAsync_SingleReleaseNoTarget_ThrowsAndDoesNotDeploy()
    {
        GivenHistory(Entry(1, 100));

        await Should.ThrowAsync<RollbackNotAvailableException>(() => _sut.RollbackDeploymentAsync(Command(releaseId: null)));

        VerifyNoDeployment();
    }

    private void GivenHistory(params RollbackReleaseHistoryEntry[] newestFirst)
        => _journal
            .Setup(journal => journal.GetSuccessfulReleaseHistoryAsync(ProjectId, EnvironmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(newestFirst.ToList());

    private Func<CreateDeploymentCommand> CaptureDelegatedCreate()
    {
        CreateDeploymentCommand captured = null;

        _deploymentService
            .Setup(service => service.CreateDeploymentAsync(It.IsAny<CreateDeploymentCommand>(), It.IsAny<CancellationToken>()))
            .Callback<CreateDeploymentCommand, CancellationToken>((command, _) => captured = command)
            .ReturnsAsync(new DeploymentCreatedEvent { TaskId = 1, Deployment = new DeploymentDto { Id = 1 } });

        return () => captured;
    }

    private void VerifyNoDeployment()
        => _deploymentService.Verify(
            service => service.CreateDeploymentAsync(It.IsAny<CreateDeploymentCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);

    private static RollbackDeploymentCommand Command(int? releaseId)
        => new() { ProjectId = ProjectId, EnvironmentId = EnvironmentId, ReleaseId = releaseId };

    private static RollbackReleaseHistoryEntry Entry(int releaseId, int deploymentId)
        => new(releaseId, $"{releaseId}.0.0", deploymentId, DateTimeOffset.UtcNow.AddMinutes(-releaseId));
}
