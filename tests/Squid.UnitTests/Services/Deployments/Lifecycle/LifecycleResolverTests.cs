using System;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Project;
using LifecycleEntity = Squid.Core.Persistence.Entities.Deployments.Lifecycle;

namespace Squid.UnitTests.Services.Deployments.Lifecycle;

public class LifecycleResolverTests
{
    private readonly Mock<IChannelDataProvider> _channelProvider = new();
    private readonly Mock<IProjectDataProvider> _projectProvider = new();
    private readonly Mock<ILifeCycleDataProvider> _lifecycleProvider = new();
    private LifecycleResolver _resolver;

    public LifecycleResolverTests()
    {
        _resolver = new LifecycleResolver(_channelProvider.Object, _projectProvider.Object, _lifecycleProvider.Object);
    }

    [Fact]
    public async Task ChannelHasLifecycle_UsesChannelLifecycle()
    {
        var channel = new Channel { Id = 1, LifecycleId = 100 };
        var lifecycle = new LifecycleEntity { Id = 100, Name = "Channel Lifecycle" };

        _channelProvider.Setup(x => x.GetChannelByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel);
        _lifecycleProvider.Setup(x => x.GetLifecycleByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lifecycle);

        var result = await _resolver.ResolveLifecycleAsync(10, 1, CancellationToken.None);

        result.Id.ShouldBe(100);
        result.Name.ShouldBe("Channel Lifecycle");
        _projectProvider.Verify(x => x.GetProjectByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChannelLifecycleNull_FallsBackToProject()
    {
        var channel = new Channel { Id = 1, LifecycleId = null };
        var project = new Core.Persistence.Entities.Deployments.Project { Id = 10, LifecycleId = 200 };
        var lifecycle = new LifecycleEntity { Id = 200, Name = "Project Lifecycle" };

        _channelProvider.Setup(x => x.GetChannelByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel);
        _projectProvider.Setup(x => x.GetProjectByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);
        _lifecycleProvider.Setup(x => x.GetLifecycleByIdAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lifecycle);

        var result = await _resolver.ResolveLifecycleAsync(10, 1, CancellationToken.None);

        result.Id.ShouldBe(200);
        result.Name.ShouldBe("Project Lifecycle");
    }

    [Fact]
    public async Task ChannelNotFound_FallsBackToProject()
    {
        var project = new Core.Persistence.Entities.Deployments.Project { Id = 10, LifecycleId = 300 };
        var lifecycle = new LifecycleEntity { Id = 300, Name = "Fallback" };

        _channelProvider.Setup(x => x.GetChannelByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Channel)null);
        _projectProvider.Setup(x => x.GetProjectByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(project);
        _lifecycleProvider.Setup(x => x.GetLifecycleByIdAsync(300, It.IsAny<CancellationToken>()))
            .ReturnsAsync(lifecycle);

        var result = await _resolver.ResolveLifecycleAsync(10, 999, CancellationToken.None);

        result.Id.ShouldBe(300);
    }

    [Fact]
    public async Task ProjectNotFound_Throws()
    {
        _channelProvider.Setup(x => x.GetChannelByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Channel { Id = 1, LifecycleId = null });
        _projectProvider.Setup(x => x.GetProjectByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Core.Persistence.Entities.Deployments.Project)null);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _resolver.ResolveLifecycleAsync(10, 1, CancellationToken.None));
    }

    [Fact]
    public async Task LifecycleNotFound_Throws()
    {
        var channel = new Channel { Id = 1, LifecycleId = 999 };

        _channelProvider.Setup(x => x.GetChannelByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel);
        _lifecycleProvider.Setup(x => x.GetLifecycleByIdAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LifecycleEntity)null);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _resolver.ResolveLifecycleAsync(10, 1, CancellationToken.None));
    }
}
