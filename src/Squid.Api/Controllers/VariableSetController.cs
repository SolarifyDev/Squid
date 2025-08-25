using Microsoft.AspNetCore.Mvc;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Requests.Deployments.Variable;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VariableSetController : ControllerBase
{
    private readonly IMediator _mediator;

    public VariableSetController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateVariableSetResponse))]
    public async Task<IActionResult> CreateVariableSetAsync([FromBody] CreateVariableSetCommand command)
    {
        var response = await _mediator.SendAsync<CreateVariableSetCommand, CreateVariableSetResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateVariableSetResponse))]
    public async Task<IActionResult> UpdateVariableSetAsync([FromBody] UpdateVariableSetCommand command)
    {
        var response = await _mediator.SendAsync<UpdateVariableSetCommand, UpdateVariableSetResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetVariableSetsResponse))]
    public async Task<IActionResult> GetVariableSetsAsync([FromQuery] GetVariableSetsRequest request)
    {
        var response = await _mediator.RequestAsync<GetVariableSetsRequest, GetVariableSetsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetVariableSetResponse))]
    public async Task<IActionResult> GetVariableSetAsync(int id)
    {
        var request = new GetVariableSetRequest { Id = id };
        var response = await _mediator.RequestAsync<GetVariableSetRequest, GetVariableSetResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteVariableSetResponse))]
    public async Task<IActionResult> DeleteVariableSetAsync([FromBody] DeleteVariableSetCommand command)
    {
        var response = await _mediator.SendAsync<DeleteVariableSetCommand, DeleteVariableSetResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

}
