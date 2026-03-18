using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Requests.Deployments.Variable;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/variable-sets")]
public class VariableSetController : ControllerBase
{
    private readonly IMediator _mediator;

    public VariableSetController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateVariableSetResponse))]
    public async Task<IActionResult> CreateVariableSetAsync([FromBody] CreateVariableSetCommand command)
    {
        var response = await _mediator.SendAsync<CreateVariableSetCommand, CreateVariableSetResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateVariableSetResponse))]
    public async Task<IActionResult> UpdateVariableSetAsync([FromBody] UpdateVariableSetCommand command)
    {
        var response = await _mediator.SendAsync<UpdateVariableSetCommand, UpdateVariableSetResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetVariableSetsResponse))]
    public async Task<IActionResult> GetVariableSetsAsync([FromQuery] GetVariableSetsRequest request)
    {
        var response = await _mediator.RequestAsync<GetVariableSetsRequest, GetVariableSetsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("detail/{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetVariableSetResponse))]
    public async Task<IActionResult> GetVariableSetAsync(int id, [FromQuery] int? spaceId = null)
    {
        var request = new GetVariableSetRequest { Id = id, SpaceId = spaceId };
        var response = await _mediator.RequestAsync<GetVariableSetRequest, GetVariableSetResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteVariableSetResponse))]
    public async Task<IActionResult> DeleteVariableSetAsync([FromBody] DeleteVariableSetCommand command)
    {
        var response = await _mediator.SendAsync<DeleteVariableSetCommand, DeleteVariableSetResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
}
