using Microsoft.AspNetCore.Authorization;
using Squid.Message.Models.Setup;

namespace Squid.Api.Controllers;

/// <summary>
/// The normal API host's half of the first-run handshake: the web UI asks this on load, and the
/// running API always answers that no setup is needed. (While setup IS needed, the API host is not
/// running at all — the setup host answers the same route instead.)
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/setup")]
public class SetupStatusController : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new SetupStatus { Required = false });
}
