using Mediator.Net.Context;
using Mediator.Net.Contracts;
using Squid.Core.Middlewares.Authorization;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;

namespace Squid.UnitTests.Middlewares;

public class AuthorizationSpecificationTests
{
    private readonly Mock<IAuthorizationService> _authorizationService = new();
    private readonly Mock<ICurrentUser> _currentUser = new();

    [Fact]
    public async Task NullCurrentUser_AllowsThrough()
    {
        _currentUser.Setup(x => x.Id).Returns((int?)null);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestSystemCommand());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MessageWithoutAttribute_AllowsThrough()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestCommandWithoutPermission());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MessageWithAttribute_ChecksPermission()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestSystemCommand());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.Permission == Permission.UserView && r.UserId == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PermissionDenied_ThrowsCorrectException()
    {
        _currentUser.Setup(x => x.Id).Returns(1);
        _authorizationService.Setup(x => x.EnsurePermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new PermissionDeniedException(Permission.UserView, "Denied"));

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestSystemCommand());

        var ex = await Should.ThrowAsync<PermissionDeniedException>(() => spec.BeforeExecute(context.Object, CancellationToken.None));

        ex.Permission.ShouldBe(Permission.UserView);
    }

    [Fact]
    public async Task SpaceScopedMessage_PassesSpaceId()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestSpaceScopedCommand());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.SpaceId == 42 && r.Permission == Permission.DeploymentCreate), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NonSpaceScopedMessage_SystemPermission_HasNullSpaceId()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestSystemCommand());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.SpaceId == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MultiplePermissionAttributes_ChecksAll()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestCommandWithMultipleSystemPermissions());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.Permission == Permission.UserView), It.IsAny<CancellationToken>()), Times.Once);
        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.Permission == Permission.UserEdit), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== SpaceOnly Enforcement ==========

    [Fact]
    public async Task SpaceOnlyPermission_WithoutSpaceId_ThrowsDenied()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestSpaceOnlyCommandWithoutScope());

        var ex = await Should.ThrowAsync<PermissionDeniedException>(() => spec.BeforeExecute(context.Object, CancellationToken.None));

        ex.Permission.ShouldBe(Permission.ProjectView);
        ex.Message.ShouldContain("SpaceOnly");
    }

    [Fact]
    public async Task SpaceOnlyPermission_WithSpaceId_PassesThrough()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestSpaceScopedCommand());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.SpaceId == 42), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SystemOnlyPermission_WithoutSpaceId_PassesThrough()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestSystemCommand());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.SpaceId == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MixedPermission_WithoutSpaceId_PassesThrough()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestMixedPermissionCommand());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.Permission == Permission.TaskView && r.SpaceId == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========== Test Helper Classes ==========

    [RequiresPermission(Permission.UserView)]
    private class TestSystemCommand : ICommand { }

    private class TestCommandWithoutPermission : ICommand { }

    [RequiresPermission(Permission.DeploymentCreate)]
    private class TestSpaceScopedCommand : ICommand, ISpaceScoped
    {
        public int? SpaceId => 42;
    }

    [RequiresPermission(Permission.ProjectView)]
    private class TestSpaceOnlyCommandWithoutScope : ICommand { }

    [RequiresPermission(Permission.UserView)]
    [RequiresPermission(Permission.UserEdit)]
    private class TestCommandWithMultipleSystemPermissions : ICommand { }

    [RequiresPermission(Permission.TaskView)]
    private class TestMixedPermissionCommand : ICommand { }
}
