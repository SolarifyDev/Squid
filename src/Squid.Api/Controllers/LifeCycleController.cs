using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Message.Requests.Deployments.LifeCycle;

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
    
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateLifeCycleResponse))]
    public async Task<IActionResult> CreateLifeCycleAsync([FromBody] CreateLifeCycleCommand command)
    {
        var response = await _mediator.SendAsync<CreateLifeCycleCommand, CreateLifeCycleResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateLifeCycleResponse))]
    public async Task<IActionResult> UpdateLifeCycleAsync([FromBody] UpdateLifeCycleCommand command)
    {
        var response = await _mediator.SendAsync<UpdateLifeCycleCommand, UpdateLifeCycleResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteLifeCyclesResponse))]
    public async Task<IActionResult> DeleteLifeCyclesAsync([FromBody] DeleteLifeCyclesCommand command)
    {
        var response = await _mediator.SendAsync<DeleteLifeCyclesCommand, DeleteLifeCyclesResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetLifeCycleResponse))]
    public async Task<IActionResult> GetLifeCyclesAsync([FromQuery] GetLifecycleRequest request)
    {
        var response = await _mediator.RequestAsync<GetLifecycleRequest, GetLifeCycleResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}