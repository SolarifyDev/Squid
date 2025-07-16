using Microsoft.AspNetCore.Mvc;
using Squid.Message.Commands.Deployments.Machine;
using Squid.Message.Requests.Deployments.Machine;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MachineController : ControllerBase
{
    private readonly IMediator _mediator;

    public MachineController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateMachineResponse))]
    public async Task<IActionResult> CreateMachineAsync([FromBody] CreateMachineCommand command)
    {
        var response = await _mediator.SendAsync<CreateMachineCommand, CreateMachineResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateMachineResponse))]
    public async Task<IActionResult> UpdateMachineAsync([FromBody] UpdateMachineCommand command)
    {
        var response = await _mediator.SendAsync<UpdateMachineCommand, UpdateMachineResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteMachineResponse))]
    public async Task<IActionResult> DeleteMachineAsync([FromBody] DeleteMachineCommand command)
    {
        var response = await _mediator.SendAsync<DeleteMachineCommand, DeleteMachineResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetMachinesResponse))]
    public async Task<IActionResult> GetMachinesAsync([FromQuery] GetMachinesRequest request)
    {
        var response = await _mediator.RequestAsync<GetMachinesRequest, GetMachinesResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
} 