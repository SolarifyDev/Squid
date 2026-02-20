using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Squid.Core.Services.Agents;
using Squid.Message.Commands.Agent;

namespace Squid.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/agents")]
public class AgentController : ControllerBase
{
    private readonly IAgentService _agentService;

    public AgentController(IAgentService agentService)
    {
        _agentService = agentService;
    }

    [HttpPost("register")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RegisterAgentResponse))]
    public async Task<IActionResult> RegisterAsync(
        [FromBody] RegisterAgentCommand command, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(command.Thumbprint))
            return BadRequest("Thumbprint is required");

        if (string.IsNullOrEmpty(command.SubscriptionId))
            return BadRequest("SubscriptionId is required");

        var result = await _agentService.RegisterAgentAsync(command, ct);

        return Ok(new RegisterAgentResponse { Data = result });
    }
}
