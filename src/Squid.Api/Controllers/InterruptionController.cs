using Squid.Message.Commands.Deployments.Interruption;
using Squid.Message.Requests.Deployments.Interruption;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api")]
public class InterruptionController : ControllerBase
{
    private readonly IMediator _mediator;

    public InterruptionController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("tasks/{taskId:int}/interruptions")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetPendingInterruptionsResponse))]
    public async Task<IActionResult> GetPendingInterruptionsAsync(int taskId)
    {
        var response = await _mediator.RequestAsync<GetPendingInterruptionsRequest, GetPendingInterruptionsResponse>(
            new GetPendingInterruptionsRequest { ServerTaskId = taskId }).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPut("interruptions/{id:int}/responsible")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(TakeResponsibilityResponse))]
    public async Task<IActionResult> TakeResponsibilityAsync(int id, [FromBody] TakeResponsibilityCommand command)
    {
        command.InterruptionId = id;

        var response = await _mediator.SendAsync<TakeResponsibilityCommand, TakeResponsibilityResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("interruptions/{id:int}/submit")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(SubmitInterruptionResponse))]
    public async Task<IActionResult> SubmitInterruptionAsync(int id, [FromBody] SubmitInterruptionCommand command)
    {
        command.InterruptionId = id;

        var response = await _mediator.SendAsync<SubmitInterruptionCommand, SubmitInterruptionResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
}
