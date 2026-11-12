using System.Collections.Generic;
using Squid.Core.Services.Deployments.ActivityLog;
using Squid.Core.Services.Deployments.ServerTask;
using ActivityLogEntity = Squid.Core.Persistence.Entities.Deployments.ActivityLog;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using ServerTaskLogEntity = Squid.Core.Persistence.Entities.Deployments.ServerTaskLog;

namespace Squid.UnitTests.Services.Deployments.ServerTask;

/// <summary>
/// The task-details read is what the deployment page polls; it carries <c>IsImported</c> so the UI can stop polling and
/// hide task controls for read-only Octopus history.
/// </summary>
public class ServerTaskServiceReadTests
{
    private readonly Mock<IServerTaskDataProvider> _dataProvider = new();
    private readonly Mock<IActivityLogDataProvider> _activityLogs = new();
    private readonly Mock<IServerTaskLogDataProvider> _logs = new();
    private readonly ServerTaskService _sut;

    public ServerTaskServiceReadTests()
    {
        _sut = new ServerTaskService(_dataProvider.Object, _activityLogs.Object, _logs.Object);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetTaskDetails_CarriesTheImportedMarker(bool imported)
    {
        _dataProvider.Setup(d => d.GetServerTaskByIdAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(new ServerTaskEntity { Id = 3, State = TaskState.Executing });
        _dataProvider.Setup(d => d.IsImportedAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(imported);
        _activityLogs.Setup(a => a.GetTreeByTaskIdAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ActivityLogEntity>());
        _logs.Setup(l => l.GetLatestUnscopedLogsByTaskAsync(3, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ServerTaskLogEntity>());

        var details = await _sut.GetTaskDetailsAsync(3);

        details.Task.IsImported.ShouldBe(imported);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsImported_DelegatesToTheDataProvider(bool imported)
    {
        _dataProvider.Setup(d => d.IsImportedAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(imported);

        (await _sut.IsImportedAsync(9)).ShouldBe(imported);
    }
}
