using Microsoft.AspNetCore.Authorization;
using Squid.Message.Commands.SystemAdmin;

namespace Squid.Api.Controllers;

/// <summary>
/// System-administration endpoints. Every action under this controller requires
/// the <c>AdministerSystem</c> permission (enforced by the per-command
/// <c>[RequiresPermission]</c> attribute in the Mediator pipeline, NOT by an
/// MVC filter -- the Mediator path is the canonical authorization seam).
/// </summary>
[ApiController]
[Authorize]
[Route("system")]
public class SystemController : ControllerBase
{
    private readonly IMediator _mediator;

    public SystemController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Rotates the shared bootstrap API key for a given install-surface (Tentacle
    /// or Kubernetes Agent). After rotation, the next <c>GenerateInstallScript</c>
    /// call mints a fresh key with the canonical description; previously-active
    /// keys are disabled (their <c>IsDisabled=true</c>) so existing audit trail
    /// stays intact.
    ///
    /// <para><b>Already-registered agents are unaffected</b> -- they use the
    /// machine identity + server thumbprint persisted at register time, not the
    /// bootstrap key.</para>
    /// </summary>
    [HttpPost("bootstrap-keys/rotate")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RotateBootstrapApiKeyResponse))]
    public async Task<IActionResult> RotateBootstrapApiKeyAsync([FromBody] RotateBootstrapApiKeyCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<RotateBootstrapApiKeyCommand, RotateBootstrapApiKeyResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }
}
