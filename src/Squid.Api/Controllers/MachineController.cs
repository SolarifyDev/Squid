using Squid.Message.Commands.Deployments.Machine;
using Squid.Message.Requests.Deployments.Machine;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/machines")]
public class MachineController : ControllerBase
{
    private readonly IMediator _mediator;

    public MachineController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateMachineResponse))]
    public async Task<IActionResult> CreateMachineAsync([FromBody] CreateMachineCommand command)
    {
        var response = await _mediator.SendAsync<CreateMachineCommand, CreateMachineResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateMachineResponse))]
    public async Task<IActionResult> UpdateMachineAsync([FromBody] UpdateMachineCommand command)
    {
        var response = await _mediator.SendAsync<UpdateMachineCommand, UpdateMachineResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteMachinesResponse))]
    public async Task<IActionResult> DeleteMachinesAsync([FromBody] DeleteMachinesCommand command)
    {
        var response = await _mediator.SendAsync<DeleteMachinesCommand, DeleteMachinesResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetMachinesResponse))]
    public async Task<IActionResult> GetMachinesAsync([FromQuery] GetMachinesRequest request)
    {
        var response = await _mediator.RequestAsync<GetMachinesRequest, GetMachinesResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
