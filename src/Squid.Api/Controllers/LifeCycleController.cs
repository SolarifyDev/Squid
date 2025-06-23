using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Api.Controllers;


[ApiController]
[Route("api/[controller]")]
public class LifeCycleController : ControllerBase
{
    private readonly IMediator _mediator;

    public LifeCycleController(IMediator mediator)
    {
        _mediator = mediator;
    }
    
    [Route("create"), HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateLifeCycleResponse))]
    public async Task<IActionResult> CreateLifeCycleAsync([FromBody] CreateLifeCycleCommand command)
    {
        var response = await _mediator.SendAsync<CreateLifeCycleCommand, CreateLifeCycleResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [Route("update"), HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateLifeCycleResponse))]
    public async Task<IActionResult> UpdateLifeCycleAsync([FromBody] UpdateLifeCycleCommand command)
    {
        var response = await _mediator.SendAsync<UpdateLifeCycleCommand, UpdateLifeCycleResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [Route("delete"), HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateLifeCycleResponse))]
    public async Task<IActionResult> DeleteLifeCyclesAsync([FromBody] DeleteLifeCyclesCommand command)
    {
        var response = await _mediator.SendAsync<DeleteLifeCyclesCommand, DeleteLifeCyclesResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
}