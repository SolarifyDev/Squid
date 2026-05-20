using System.Threading;
using System.Threading.Tasks;
using Mediator.Net.Context;
using Squid.Core.Handlers.CommandHandlers.SystemAdmin;
using Squid.Core.Services.Account;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.SystemAdmin;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.SystemAdmin;

/// <summary>
/// Pins the rotation handler's contract:
/// <list type="bullet">
///   <item>Surface name maps to the canonical bootstrap description used by
///         <see cref="MachineScriptService"/>'s get-or-create flow.</item>
///   <item>Calls <see cref="IAccountService.DisableApiKeysByDescriptionAsync"/>
///         with InternalUser as the key owner.</item>
///   <item>Returns the disabled count so operators can confirm rotation took effect.</item>
///   <item>Unknown surface name throws ArgumentException with the expected values
///         listed (operator-friendly error).</item>
/// </list>
/// </summary>
public class RotateBootstrapApiKeyCommandHandlerTests
{
    private readonly Mock<IAccountService> _accountService = new();

    [Fact]
    public async Task Handle_TentacleSurface_DisablesViaTentacleDescription()
    {
        _accountService
            .Setup(x => x.DisableApiKeysByDescriptionAsync(CurrentUsers.InternalUser.Id, MachineScriptService.TentacleBootstrapKeyDescription, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var handler = new RotateBootstrapApiKeyCommandHandler(_accountService.Object);
        var response = await InvokeAsync(handler, new RotateBootstrapApiKeyCommand { Surface = "Tentacle" });

        response.Data.ShouldNotBeNull();
        response.Data.Description.ShouldBe(MachineScriptService.TentacleBootstrapKeyDescription);
        response.Data.DisabledCount.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_KubernetesAgentSurface_DisablesViaKubernetesDescription()
    {
        _accountService
            .Setup(x => x.DisableApiKeysByDescriptionAsync(CurrentUsers.InternalUser.Id, MachineScriptService.KubernetesAgentBootstrapKeyDescription, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var handler = new RotateBootstrapApiKeyCommandHandler(_accountService.Object);
        var response = await InvokeAsync(handler, new RotateBootstrapApiKeyCommand { Surface = "KubernetesAgent" });

        response.Data.Description.ShouldBe(MachineScriptService.KubernetesAgentBootstrapKeyDescription);
        response.Data.DisabledCount.ShouldBe(1);
    }

    [Fact]
    public async Task Handle_SurfaceCaseInsensitive_StillResolves()
    {
        _accountService
            .Setup(x => x.DisableApiKeysByDescriptionAsync(CurrentUsers.InternalUser.Id, MachineScriptService.TentacleBootstrapKeyDescription, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var handler = new RotateBootstrapApiKeyCommandHandler(_accountService.Object);
        var response = await InvokeAsync(handler, new RotateBootstrapApiKeyCommand { Surface = "tentacle" });

        response.Data.Description.ShouldBe(MachineScriptService.TentacleBootstrapKeyDescription,
            customMessage: "Surface comparison must be case-insensitive -- operators won't always title-case the name.");
    }

    [Fact]
    public async Task Handle_FirstEverRotation_NoExistingKeys_ReturnsZeroDisabledCount()
    {
        _accountService
            .Setup(x => x.DisableApiKeysByDescriptionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var handler = new RotateBootstrapApiKeyCommandHandler(_accountService.Object);
        var response = await InvokeAsync(handler, new RotateBootstrapApiKeyCommand { Surface = "Tentacle" });

        response.Data.DisabledCount.ShouldBe(0,
            customMessage: "First-ever rotation (no existing key yet) is harmless -- returns 0 instead of throwing.");
    }

    [Fact]
    public async Task Handle_UnknownSurface_ThrowsWithActionableMessage()
    {
        var handler = new RotateBootstrapApiKeyCommandHandler(_accountService.Object);
        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            InvokeAsync(handler, new RotateBootstrapApiKeyCommand { Surface = "FoobarAgent" }));

        ex.Message.ShouldContain("FoobarAgent");
        ex.Message.ShouldContain("Tentacle");
        ex.Message.ShouldContain("KubernetesAgent");
    }

    private static async Task<RotateBootstrapApiKeyResponse> InvokeAsync(RotateBootstrapApiKeyCommandHandler handler, RotateBootstrapApiKeyCommand command)
    {
        var context = new ReceiveContext<RotateBootstrapApiKeyCommand>(command);
        return await handler.Handle(context, CancellationToken.None);
    }
}
