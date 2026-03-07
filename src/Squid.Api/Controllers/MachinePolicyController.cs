using Squid.Message.Commands.Machine;
using Squid.Message.Requests.Machines;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/machine-policies")]
public class MachinePolicyController : ControllerBase
{
    private readonly IMediator _mediator;

    public MachinePolicyController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetMachinePoliciesResponse))]
    public async Task<IActionResult> GetMachinePoliciesAsync(CancellationToken ct)
    {
        var response = await _mediator.RequestAsync<GetMachinePoliciesRequest, GetMachinePoliciesResponse>(new GetMachinePoliciesRequest(), ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetMachinePolicyResponse))]
    public async Task<IActionResult> GetMachinePolicyAsync(int id, CancellationToken ct)
    {
        var request = new GetMachinePolicyRequest { Id = id };
        var response = await _mediator.RequestAsync<GetMachinePolicyRequest, GetMachinePolicyResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("save")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(SaveMachinePolicyResponse))]
    public async Task<IActionResult> SaveMachinePolicyAsync([FromBody] SaveMachinePolicyCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<SaveMachinePolicyCommand, SaveMachinePolicyResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteMachinePolicyResponse))]
    public async Task<IActionResult> DeleteMachinePolicyAsync([FromBody] DeleteMachinePolicyCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<DeleteMachinePolicyCommand, DeleteMachinePolicyResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }
}
