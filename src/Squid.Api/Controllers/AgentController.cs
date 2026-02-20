using Squid.Message.Commands.Agent;
using Microsoft.AspNetCore.Authorization;

namespace Squid.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/agents")]
public class AgentController : ControllerBase
{
    private readonly IMediator _mediator;

    public AgentController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("register")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RegisterAgentResponse))]
    public async Task<IActionResult> RegisterAsync([FromBody] RegisterAgentCommand command, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(command.Thumbprint)) return BadRequest("Thumbprint is required");

        if (string.IsNullOrEmpty(command.SubscriptionId)) return BadRequest("SubscriptionId is required");

        var response = await _mediator.SendAsync<RegisterAgentCommand, RegisterAgentResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }
}
