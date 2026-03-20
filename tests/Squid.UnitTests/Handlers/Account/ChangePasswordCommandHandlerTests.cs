using Squid.Core.Handlers.CommandHandlers.Account;
using Squid.Core.Services.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Message.Commands.Account;
using Squid.Message.Enums;

namespace Squid.UnitTests.Handlers.Account;

public class ChangePasswordCommandHandlerTests
{
    private readonly Mock<IAccountService> _accountService = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IAuthorizationService> _authorizationService = new();

    private ChangePasswordCommandHandler CreateHandler() =>
        new(_accountService.Object, _currentUser.Object, _authorizationService.Object);

    private static Mock<IReceiveContext<ChangePasswordCommand>> CreateContext(ChangePasswordCommand command)
    {
        var context = new Mock<IReceiveContext<ChangePasswordCommand>>();
        context.Setup(c => c.Message).Returns(command);
        return context;
    }

    [Fact]
    public async Task SelfChange_CallsServiceWithIsSelfTrue()
    {
        _currentUser.Setup(u => u.Id).Returns(1);

        var command = new ChangePasswordCommand { UserId = 1, CurrentPassword = "OldPass123", NewPassword = "NewPass123" };
        var handler = CreateHandler();

        await handler.Handle(CreateContext(command).Object, CancellationToken.None);

        _accountService.Verify(s => s.ChangePasswordAsync(1, "OldPass123", "NewPass123", true, It.IsAny<CancellationToken>()), Times.Once);
        _authorizationService.Verify(s => s.EnsurePermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AdminReset_ChecksUserEditPermission()
    {
        _currentUser.Setup(u => u.Id).Returns(1);

        var command = new ChangePasswordCommand { UserId = 2, NewPassword = "ResetPass123" };
        var handler = CreateHandler();

        await handler.Handle(CreateContext(command).Object, CancellationToken.None);

        _authorizationService.Verify(s => s.EnsurePermissionAsync(
            It.Is<PermissionCheckRequest>(r => r.UserId == 1 && r.Permission == Permission.UserEdit), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AdminReset_CallsServiceWithIsSelfFalse()
    {
        _currentUser.Setup(u => u.Id).Returns(1);

        var command = new ChangePasswordCommand { UserId = 2, NewPassword = "ResetPass123" };
        var handler = CreateHandler();

        await handler.Handle(CreateContext(command).Object, CancellationToken.None);

        _accountService.Verify(s => s.ChangePasswordAsync(2, null, "ResetPass123", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AdminReset_NoPermission_ThrowsPermissionDenied()
    {
        _currentUser.Setup(u => u.Id).Returns(1);
        _authorizationService.Setup(s => s.EnsurePermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PermissionDeniedException(Permission.UserEdit, "Not authorized"));

        var command = new ChangePasswordCommand { UserId = 2, NewPassword = "ResetPass123" };
        var handler = CreateHandler();

        await Should.ThrowAsync<PermissionDeniedException>(
            () => handler.Handle(CreateContext(command).Object, CancellationToken.None));

        _accountService.Verify(s => s.ChangePasswordAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
