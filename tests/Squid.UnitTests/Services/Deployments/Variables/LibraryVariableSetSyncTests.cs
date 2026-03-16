using System.Collections.Generic;
using Moq;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.Security;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Variables;

public class LibraryVariableSetSyncTests
{
    private readonly Mock<IMapper> _mapper = new();
    private readonly Mock<IVariableDataProvider> _variableDataProvider = new();
    private readonly Mock<IVariableScopeDataProvider> _variableScopeDataProvider = new();
    private readonly Mock<ILibraryVariableSetDataProvider> _libraryVariableSetDataProvider = new();
    private readonly SensitiveVariableHandler _sensitiveVariableHandler = new();
    private readonly IVariableService _service;

    public LibraryVariableSetSyncTests()
    {
        _service = new VariableService(
            _mapper.Object,
            _variableDataProvider.Object,
            _variableScopeDataProvider.Object,
            _sensitiveVariableHandler,
            _libraryVariableSetDataProvider.Object);
    }

    [Fact]
    public async Task UpdateVariableSet_LibraryType_SyncsName()
    {
        var variableSet = new VariableSet
        {
            Id = 10, Name = "OldName", OwnerId = 5,
            OwnerType = VariableSetOwnerType.LibraryVariableSet, SpaceId = 1
        };

        var lvs = new LibraryVariableSet { Id = 5, Name = "OldName", VariableSetId = 10, SpaceId = 1 };

        _variableDataProvider
            .Setup(v => v.GetVariableSetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(variableSet);

        _libraryVariableSetDataProvider
            .Setup(l => l.GetByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lvs);

        _mapper.Setup(m => m.Map<VariableSetDto>(It.IsAny<VariableSet>())).Returns(new VariableSetDto());

        var command = new UpdateVariableSetCommand
        {
            Id = 10, Name = "NewName", Description = "desc",
            OwnerId = 5, OwnerType = VariableSetOwnerType.LibraryVariableSet, SpaceId = 1
        };

        await _service.UpdateVariableSetAsync(command, CancellationToken.None);

        lvs.Name.ShouldBe("NewName");
        _libraryVariableSetDataProvider.Verify(l => l.UpdateAsync(lvs, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateVariableSet_ProjectType_DoesNotTouchLVS()
    {
        var variableSet = new VariableSet
        {
            Id = 10, Name = "OldName", OwnerId = 1,
            OwnerType = VariableSetOwnerType.Project, SpaceId = 1
        };

        _variableDataProvider
            .Setup(v => v.GetVariableSetByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(variableSet);

        _mapper.Setup(m => m.Map<VariableSetDto>(It.IsAny<VariableSet>())).Returns(new VariableSetDto());

        var command = new UpdateVariableSetCommand
        {
            Id = 10, Name = "NewName", Description = "desc",
            OwnerId = 1, OwnerType = VariableSetOwnerType.Project, SpaceId = 1
        };

        await _service.UpdateVariableSetAsync(command, CancellationToken.None);

        _libraryVariableSetDataProvider.Verify(l => l.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _libraryVariableSetDataProvider.Verify(l => l.UpdateAsync(It.IsAny<LibraryVariableSet>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
