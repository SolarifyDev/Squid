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

    // ── P1-D.6 (Phase-7): null-Id is now fail-closed for permissioned commands ─
    //
    // Pre-fix: `if (_currentUser.Id == null) return;` silently bypassed every
    // permission check on a permissioned command. Any auth flow that produced
    // a null Id (no token / malformed token / missing claim) skipped
    // authorization. Combined with ApiUser fall-back to InternalUser.Id when
    // HttpContext was null, this gave a "DI mishap → ApiUser sees null
    // HttpContext → falls back to 8888 → middleware bypasses" path.
    //
    // Post-fix: bypass keys off `IsInternal` (concrete-type signal); null Id
    // throws `PermissionDeniedException` for permissioned commands. Commands
    // without `[RequiresPermission]` still pass through (no permission, no
    // need for identity).

    [Fact]
    public async Task NullCurrentUser_OnUnpermissionedCommand_AllowsThrough()
    {
        _currentUser.Setup(x => x.Id).Returns((int?)null);
        _currentUser.Setup(x => x.IsInternal).Returns(false);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestCommandWithoutPermission());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NullCurrentUser_OnPermissionedCommand_ThrowsPermissionDenied()
    {
        // The actual D.6 regression: null Id on a permissioned command must
        // be REJECTED, not bypassed.
        _currentUser.Setup(x => x.Id).Returns((int?)null);
        _currentUser.Setup(x => x.IsInternal).Returns(false);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestSystemCommand());

        var ex = await Should.ThrowAsync<PermissionDeniedException>(
            async () => await spec.BeforeExecute(context.Object, CancellationToken.None));

        ex.Message.ShouldContain("Authorization rejected",
            customMessage: "the rejection message must clearly identify it as authorization-driven, not a generic exception.");
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

    // ========== Multiple Permissions AND Logic ==========

    [Fact]
    public async Task MultiplePermissions_FirstDenied_DoesNotCheckSecond()
    {
        _currentUser.Setup(x => x.Id).Returns(1);
        _authorizationService
            .Setup(x => x.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.Permission == Permission.UserView), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PermissionDeniedException(Permission.UserView, "Denied"));

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestCommandWithMultipleSystemPermissions());

        await Should.ThrowAsync<PermissionDeniedException>(() => spec.BeforeExecute(context.Object, CancellationToken.None));

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.Permission == Permission.UserEdit), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InternalUser_IsInternalTrue_SkipsAllChecks()
    {
        // P1-D.6: bypass keyed off IsInternal, NOT off Id == 8888. A real
        // InternalUser instance returns IsInternal=true; that's the safe
        // signal. An ApiUser stuck in a non-HTTP scope never returns
        // IsInternal=true so it can no longer impersonate this path.
        _currentUser.Setup(x => x.Id).Returns(Message.Constants.CurrentUsers.InternalUser.Id);
        _currentUser.Setup(x => x.IsInternal).Returns(true);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestSystemCommand());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApiUserWithInternalUserId_ButIsInternalFalse_DoesNotBypass()
    {
        // The actual D.6 regression: an ApiUser that happens to return Id=8888
        // (because it sat in a non-HTTP scope and pre-fix fell back to that
        // value) must NOT bypass authorization. The IsInternal=false signal
        // closes that path.
        _currentUser.Setup(x => x.Id).Returns(Message.Constants.CurrentUsers.InternalUser.Id);
        _currentUser.Setup(x => x.IsInternal).Returns(false);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestSystemCommand());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>()), Times.Once,
            failMessage: "ApiUser-with-fallback-Id must NOT bypass authorization. The IsInternal=false signal must force a real permission check.");
    }

    [Fact]
    public async Task MixedPermission_WithSpaceId_PassesSpaceId()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        var spec = new AuthorizationSpecification<IContext<IMessage>>(_authorizationService.Object, _currentUser.Object);
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(new TestMixedSpaceScopedCommand());

        await spec.BeforeExecute(context.Object, CancellationToken.None);

        _authorizationService.Verify(x => x.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.Permission == Permission.TaskView && r.SpaceId == 7), It.IsAny<CancellationToken>()), Times.Once);
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

    [RequiresPermission(Permission.TaskView)]
    private class TestMixedSpaceScopedCommand : ICommand, ISpaceScoped
    {
        public int? SpaceId => 7;
    }
}
