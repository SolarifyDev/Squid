using System.Net;
using Squid.Core.Services.Account;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.SystemAdmin;
using Squid.Message.Constants;
using Serilog;

namespace Squid.Core.Handlers.CommandHandlers.SystemAdmin;

/// <summary>
/// Rotates the shared bootstrap API key for the named surface (Tentacle or
/// Kubernetes Agent). Disables every existing active key with the canonical
/// description for that surface; the next <c>GenerateInstallScript</c> call
/// mints a fresh one via the get-or-create flow.
///
/// <para><b>Caller permission</b>: <c>Permission.AdministerSystem</c>, enforced
/// via <c>[RequiresPermission]</c> on the command type. Only System Administrators
/// can rotate shared keys.</para>
///
/// <para><b>Effect on already-registered agents</b>: none. They keep working --
/// the bootstrap key is only used during register; afterwards agents poll the
/// server using their persisted machine identity + server thumbprint.</para>
/// </summary>
public class RotateBootstrapApiKeyCommandHandler(IAccountService accountService) : ICommandHandler<RotateBootstrapApiKeyCommand, RotateBootstrapApiKeyResponse>
{
    public async Task<RotateBootstrapApiKeyResponse> Handle(IReceiveContext<RotateBootstrapApiKeyCommand> context, CancellationToken cancellationToken)
    {
        var description = ResolveDescription(context.Message.Surface);
        var disabledCount = await accountService.DisableApiKeysByDescriptionAsync(CurrentUsers.InternalUser.Id, description, cancellationToken).ConfigureAwait(false);

        Log.Information("Rotated bootstrap API key for surface {Surface} -- disabled {Count} previous key(s)", context.Message.Surface, disabledCount);

        return new RotateBootstrapApiKeyResponse
        {
            Code = HttpStatusCode.OK,
            Data = new RotateBootstrapApiKeyResponseData { Description = description, DisabledCount = disabledCount }
        };
    }

    /// <summary>
    /// Maps the operator-supplied surface name to the canonical description used by
    /// the install-script generators. Case-insensitive on the surface input so
    /// "tentacle" / "Tentacle" / "TENTACLE" all work.
    /// </summary>
    private static string ResolveDescription(string surface)
    {
        if (string.Equals(surface, "Tentacle", StringComparison.OrdinalIgnoreCase))
            return MachineScriptService.TentacleBootstrapKeyDescription;

        if (string.Equals(surface, "KubernetesAgent", StringComparison.OrdinalIgnoreCase))
            return MachineScriptService.KubernetesAgentBootstrapKeyDescription;

        throw new ArgumentException($"Unsupported bootstrap surface '{surface}'. Expected 'Tentacle' or 'KubernetesAgent'.", nameof(surface));
    }
}
