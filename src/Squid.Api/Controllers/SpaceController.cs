using Squid.Message.Commands.Spaces;
using Squid.Message.Requests.Spaces;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/spaces")]
public class SpaceController : ControllerBase
{
    private readonly IMediator _mediator;

    public SpaceController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetSpacesResponse))]
    public async Task<IActionResult> GetAllAsync()
    {
        var response = await _mediator.RequestAsync<GetSpacesRequest, GetSpacesResponse>(new GetSpacesRequest()).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetSpaceResponse))]
    public async Task<IActionResult> GetByIdAsync(int id)
    {
        var response = await _mediator.RequestAsync<GetSpaceRequest, GetSpaceResponse>(new GetSpaceRequest { Id = id }).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateSpaceResponse))]
    public async Task<IActionResult> CreateAsync([FromBody] CreateSpaceCommand command)
    {
        var response = await _mediator.SendAsync<CreateSpaceCommand, CreateSpaceResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateSpaceResponse))]
    public async Task<IActionResult> UpdateAsync([FromBody] UpdateSpaceCommand command)
    {
        var response = await _mediator.SendAsync<UpdateSpaceCommand, UpdateSpaceResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteSpaceResponse))]
    public async Task<IActionResult> DeleteAsync([FromBody] DeleteSpaceCommand command)
    {
        var response = await _mediator.SendAsync<DeleteSpaceCommand, DeleteSpaceResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:int}/managers")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetSpaceManagersResponse))]
    public async Task<IActionResult> GetManagersAsync(int id)
    {
        var response = await _mediator.RequestAsync<GetSpaceManagersRequest, GetSpaceManagersResponse>(new GetSpaceManagersRequest { SpaceId = id }).ConfigureAwait(false);

        return Ok(response);
    }
}
