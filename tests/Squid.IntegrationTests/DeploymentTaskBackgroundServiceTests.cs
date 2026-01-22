using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Moq;
using Shouldly;
using Squid.Core;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments;
using Squid.Core.Services.Deployments.DeploymentCompletion;
using Squid.Core.Services.Tentacle;
using Squid.IntegrationTests.Builders;
using Squid.IntegrationTests.Fixtures;
using Xunit;

namespace Squid.IntegrationTests;

[Collection("Sequential")]
public class DeploymentTaskBackgroundServiceTests : TestBase<DeploymentTaskBackgroundServiceTests>
{
    [Fact]
    public async Task RunAsync_ShouldProcessPendingDeploymentTask_AndMarkTaskSuccess()
    {
        var taskId = await BuildDeploymentDataAsync();

        var service = Resolve<DeploymentTaskBackgroundService>(builder =>
        {
            builder.RegisterMockScriptService();
            builder.RegisterSecuritySetting();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.RunAsync(cts.Token);

        await AssertTaskSuccessAsync(taskId);
        await AssertDeploymentCompletionAsync(taskId);
    }

    [Fact]
    public async Task RunAsync_ShouldMarkTaskFailed_WhenDeploymentFails()
    {
        var taskId = await BuildDeploymentDataAsync();

        var service = Resolve<DeploymentTaskBackgroundService>(builder =>
        {
            var scriptServiceMock = new Mock<IAsyncScriptService>();
            scriptServiceMock
                .Setup(x => x.StartScriptAsync(It.IsAny<StartScriptCommand>()))
                .ReturnsAsync(new ScriptTicket("fail-ticket"));
            scriptServiceMock
                .SetupSequence(x => x.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
                .ReturnsAsync(new ScriptStatusResponse(new ScriptTicket("fail-ticket"), ProcessState.Running, 1, new List<ProcessOutput>(), 0));
            builder.RegisterInstance(scriptServiceMock.Object).As<IAsyncScriptService>().SingleInstance();
            builder.RegisterSecuritySetting();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await service.RunAsync(cts.Token);
        }
        catch
        {
        }

        await AssertTaskStateAsync(taskId, "Failed");
    }

    [Fact]
    public async Task RunAsync_ShouldProcessMultipleTasksInSequence()
    {
        var taskId1 = await BuildDeploymentDataAsync(builder =>
        {
            builder.WithServerTask(t => t.Name = "Task 1");
        });

        var taskId2 = await BuildDeploymentDataAsync(builder =>
        {
            builder.WithServerTask(t => t.Name = "Task 2");
        });

        var service = Resolve<DeploymentTaskBackgroundService>(builder =>
        {
            builder.RegisterMockScriptService();
            builder.RegisterSecuritySetting();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await service.RunAsync(cts.Token);

        await AssertTaskSuccessAsync(taskId1);
        await AssertTaskSuccessAsync(taskId2);
    }
}
