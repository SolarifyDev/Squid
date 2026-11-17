using System.Collections.Generic;
using System.Linq;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.Deployments.Variables.Exceptions;
using Squid.Core.Services.Security;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Variables;

/// <summary>
/// A save replaces the whole set (delete, then re-insert what was posted). Posted with the version it read, a save over a set another
/// writer moved since (an Octopus import appended variables, another editor saved) is refused instead of erasing that writer's rows.
/// </summary>
public class VariableSetVersionConflictTests
{
    private const int SetId = 41;
    private const int StoredVersion = 3;

    [Theory]
    [InlineData(null, false)]          // a caller that does not say which version it read keeps the old behaviour
    [InlineData(StoredVersion, false)] // read at the current version: saved
    [InlineData(2, true)]              // read before the import appended its variables: refused
    public async Task UpdateVariableSet_PostedVersion_SavedOnlyOverTheVersionItRead(int? postedVersion, bool refused)
    {
        var dataProvider = new Mock<IVariableDataProvider>();
        dataProvider.Setup(p => p.GetVariableSetByIdAsync(SetId, It.IsAny<CancellationToken>())).ReturnsAsync(new VariableSet { Id = SetId, OwnerType = VariableSetOwnerType.Project, Version = StoredVersion });
        MaskedSensitiveValuesTests.RunLockedActions(dataProvider);
        var service = new VariableService(Mapper(), dataProvider.Object, new Mock<IVariableScopeDataProvider>().Object, new Mock<ILibraryVariableSetDataProvider>().Object, new VariableSecretCollaborators(new SensitiveVariableHandler()));
        var command = new UpdateVariableSetCommand { Id = SetId, Name = "Project variables", OwnerType = VariableSetOwnerType.Project, Version = postedVersion, Variables = [new VariableModel { Name = "Plain", Type = VariableType.String, Value = "x" }] };

        if (refused)
        {
            var ex = await Should.ThrowAsync<VariableSetVersionConflictException>(() => service.UpdateVariableSetAsync(command, CancellationToken.None));

            (ex.VariableSetId, ex.ExpectedVersion, ex.CurrentVersion).ShouldBe((SetId, 2, StoredVersion));
        }
        else
        {
            await service.UpdateVariableSetAsync(command, CancellationToken.None);
        }

        dataProvider.Verify(p => p.DeleteVariablesByVariableSetIdAsync(SetId, It.IsAny<CancellationToken>()), refused ? Times.Never() : Times.Once());
        dataProvider.Verify(p => p.UpdateVariableSetAsync(It.IsAny<VariableSet>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), refused ? Times.Never() : Times.Once());
    }

    private static IMapper Mapper()
    {
        var mapper = new Mock<IMapper>();

        mapper.Setup(m => m.Map<Variable>(It.IsAny<VariableModel>())).Returns<VariableModel>(m => new Variable { Name = m.Name, Value = m.Value, Type = m.Type });
        mapper.Setup(m => m.Map<VariableSetDto>(It.IsAny<VariableSet>())).Returns(new VariableSetDto());

        return mapper.Object;
    }
}
