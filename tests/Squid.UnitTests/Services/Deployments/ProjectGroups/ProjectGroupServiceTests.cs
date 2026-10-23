using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.ProjectGroup;
using Squid.Message.Commands.Deployments.ProjectGroup;
using Squid.Message.Models.Deployments.ProjectGroup;

namespace Squid.UnitTests.Services.Deployments.ProjectGroups;

public class ProjectGroupServiceTests
{
    private readonly Mock<IProjectGroupDataProvider> _dataProvider = new();
    private readonly ProjectGroupService _sut;

    public ProjectGroupServiceTests()
    {
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile<ProjectGroupMapping>()).CreateMapper();
        _sut = new ProjectGroupService(mapper, _dataProvider.Object);
    }

    [Fact]
    public async Task UpdateProjectGroupAsync_ExistingGroup_UpdatesInPlace()
    {
        var existing = new ProjectGroup
        {
            Id = 10,
            Name = "Old",
            Description = "Old description",
            SpaceId = 1,
            Slug = "old"
        };

        _dataProvider
            .Setup(p => p.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var command = new UpdateProjectGroupCommand
        {
            Id = 10,
            ProjectGroup = new CreateOrUpdateProjectGroupModel
            {
                Name = "New",
                Description = "New description",
                SpaceId = 1,
                Slug = "new"
            }
        };

        var result = await _sut.UpdateProjectGroupAsync(command, CancellationToken.None);

        existing.Id.ShouldBe(10);
        existing.Name.ShouldBe("New");
        existing.Description.ShouldBe("New description");
        existing.Slug.ShouldBe("new");
        result.Data.Id.ShouldBe(10);
        result.Data.Name.ShouldBe("New");

        _dataProvider.Verify(
            p => p.UpdateAsync(existing, true, It.IsAny<CancellationToken>()),
            Times.Once);
        _dataProvider.Verify(
            p => p.AddAsync(It.IsAny<ProjectGroup>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateProjectGroupAsync_ProjectGroupIdInPayload_IsSupported()
    {
        var existing = new ProjectGroup
        {
            Id = 10,
            Name = "Old",
            Description = "Old description",
            SpaceId = 1,
            Slug = "old"
        };

        _dataProvider
            .Setup(p => p.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var command = new UpdateProjectGroupCommand
        {
            ProjectGroup = new CreateOrUpdateProjectGroupModel
            {
                Id = 10,
                Name = "New",
                SpaceId = 1
            }
        };

        var result = await _sut.UpdateProjectGroupAsync(command, CancellationToken.None);

        result.Data.Id.ShouldBe(10);
        existing.Name.ShouldBe("New");
        existing.SpaceId.ShouldBe(1);
    }

    [Fact]
    public async Task UpdateProjectGroupAsync_WithoutSpaceId_PreservesExistingSpace()
    {
        var existing = new ProjectGroup
        {
            Id = 10,
            Name = "Old",
            Description = "Old description",
            SpaceId = 1,
            Slug = "old"
        };

        _dataProvider
            .Setup(p => p.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var command = new UpdateProjectGroupCommand
        {
            Id = 10,
            ProjectGroup = new CreateOrUpdateProjectGroupModel
            {
                Name = "New",
                Description = "New description"
            }
        };

        await _sut.UpdateProjectGroupAsync(command, CancellationToken.None);

        existing.SpaceId.ShouldBe(1);
        existing.Slug.ShouldBe("old");
        existing.Description.ShouldBe("New description");
    }

    [Fact]
    public async Task UpdateProjectGroupAsync_NotFound_Throws()
    {
        _dataProvider
            .Setup(p => p.GetByIdAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProjectGroup)null);

        var command = new UpdateProjectGroupCommand
        {
            Id = 99,
            ProjectGroup = new CreateOrUpdateProjectGroupModel
            {
                Name = "New",
                SpaceId = 1
            }
        };

        await Should.ThrowAsync<DeploymentEntityNotFoundException>(
            () => _sut.UpdateProjectGroupAsync(command, CancellationToken.None));

        _dataProvider.Verify(
            p => p.UpdateAsync(It.IsAny<ProjectGroup>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateProjectGroupAsync_DifferentSpace_Throws()
    {
        var existing = new ProjectGroup
        {
            Id = 10,
            Name = "Old",
            SpaceId = 1
        };

        _dataProvider
            .Setup(p => p.GetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var command = new UpdateProjectGroupCommand
        {
            Id = 10,
            ProjectGroup = new CreateOrUpdateProjectGroupModel
            {
                Name = "New",
                SpaceId = 2
            }
        };

        await Should.ThrowAsync<DeploymentEntityNotFoundException>(
            () => _sut.UpdateProjectGroupAsync(command, CancellationToken.None));

        _dataProvider.Verify(
            p => p.UpdateAsync(It.IsAny<ProjectGroup>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
