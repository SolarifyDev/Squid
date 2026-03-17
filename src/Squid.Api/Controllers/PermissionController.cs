using Squid.Message.Requests.Authorization;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/permissions")]
public class PermissionController : ControllerBase
{
    private readonly IMediator _mediator;

    public PermissionController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("all")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetPermissionsResponse))]
    public async Task<IActionResult> GetAllAsync()
    {
        var response = await _mediator.RequestAsync<GetPermissionsRequest, GetPermissionsResponse>(new GetPermissionsRequest()).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("me")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetMyPermissionsResponse))]
    public async Task<IActionResult> GetMyPermissionsAsync()
    {
        var response = await _mediator.RequestAsync<GetMyPermissionsRequest, GetMyPermissionsResponse>(new GetMyPermissionsRequest()).ConfigureAwait(false);

        return Ok(response);
    }
}
