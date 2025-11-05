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
    
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateReleaseResponse))]
    public async Task<IActionResult> UpdateReleaseAsync([FromBody] UpdateReleaseCommand command)
    {
        var response = await _mediator.SendAsync<UpdateReleaseCommand, UpdateReleaseResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpPut("variable")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateReleaseVariableAsync([FromBody] UpdateReleaseVariableCommand command)
    {
        await _mediator.SendAsync(command).ConfigureAwait(false);

        return Ok();
    }
    
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteReleaseResponse))]
    public async Task<IActionResult> DeleteReleaseAsync([FromBody] DeleteReleaseCommand command)
    {
        var response = await _mediator.SendAsync<DeleteReleaseCommand, DeleteReleaseResponse>(command).ConfigureAwait(false);

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