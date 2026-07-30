using System.Linq;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Process.Action;

namespace Squid.UnitTests.Services.Deployments.Process;

public class DeploymentActionPropertyDataProviderTests
{
    [Fact]
    public async Task AddDeploymentActionPropertiesAsync_DeduplicatesByActionIdAndPropertyName_LastValueWins()
    {
        var repository = new Mock<IRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var inserted = new List<DeploymentActionProperty>();

        repository
            .Setup(r => r.InsertAllAsync(It.IsAny<IEnumerable<DeploymentActionProperty>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<DeploymentActionProperty>, CancellationToken>((entities, _) => inserted = entities.ToList())
            .Returns(Task.CompletedTask);

        unitOfWork
            .Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var provider = new DeploymentActionPropertyDataProvider(repository.Object, unitOfWork.Object);

        await provider.AddDeploymentActionPropertiesAsync(
            [
                new DeploymentActionProperty { ActionId = 10, PropertyName = "Squid.Action.WindowsService.ServiceName", PropertyValue = "old-service" },
                new DeploymentActionProperty { ActionId = 10, PropertyName = "squid.action.windowsservice.servicename", PropertyValue = "new-service" },
                new DeploymentActionProperty { ActionId = 10, PropertyName = "Squid.Action.WindowsService.ExecutablePath", PropertyValue = "Demo.WindowsService.exe" },
                new DeploymentActionProperty { ActionId = 11, PropertyName = "Squid.Action.WindowsService.ServiceName", PropertyValue = "other-action-service" }
            ],
            CancellationToken.None);

        inserted.Count.ShouldBe(3);
        inserted.ShouldContain(p => p.ActionId == 10
                                    && p.PropertyName == "Squid.Action.WindowsService.ServiceName"
                                    && p.PropertyValue == "new-service");
        inserted.ShouldContain(p => p.ActionId == 10
                                    && p.PropertyName == "Squid.Action.WindowsService.ExecutablePath"
                                    && p.PropertyValue == "Demo.WindowsService.exe");
        inserted.ShouldContain(p => p.ActionId == 11
                                    && p.PropertyName == "Squid.Action.WindowsService.ServiceName"
                                    && p.PropertyValue == "other-action-service");
        unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
