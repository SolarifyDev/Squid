using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Requests.Deployments.Release;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReleaseController : ControllerBase
{
    private readonly IMediator _mediator;

    public ReleaseController(IMediator mediator)
    {
        _mediator = mediator;
    }
    
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateReleaseResponse))]
    public async Task<IActionResult> CreateReleaseAsync([FromBody] CreateReleaseCommand command)
    {
        var response = await _mediator.SendAsync<CreateReleaseCommand, CreateReleaseResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetReleasesResponse))]
    public async Task<IActionResult> GetReleasesAsync([FromQuery] GetReleasesRequest request)
    {
        var response = await _mediator.RequestAsync<GetReleasesRequest, GetReleasesResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}