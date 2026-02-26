using Squid.Message.Commands.Machine;
using Squid.Message.Requests.Machines;
using Microsoft.AspNetCore.Authorization;

namespace Squid.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/machines")]
public class MachineController : ControllerBase
{
    private readonly IMediator _mediator;

    public MachineController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("register/kubernetes-agent")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RegisterMachineResponse))]
    public async Task<IActionResult> RegisterKubernetesAgentAsync([FromBody] RegisterKubernetesAgentCommand command, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(command.Thumbprint)) return BadRequest("Thumbprint is required");

        if (string.IsNullOrEmpty(command.SubscriptionId)) return BadRequest("SubscriptionId is required");

        var response = await _mediator.SendAsync<RegisterKubernetesAgentCommand, RegisterMachineResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetMachinesResponse))]
    public async Task<IActionResult> GetMachinesAsync([FromQuery] GetMachinesRequest request, CancellationToken ct)
    {
        var response = await _mediator.RequestAsync<GetMachinesRequest, GetMachinesResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("generate-kubernetes-agent-install-script")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GenerateKubernetesAgentInstallScriptResponse))]
    public async Task<IActionResult> GenerateKubernetesAgentInstallScriptAsync(
        [FromBody] GenerateKubernetesAgentInstallScriptCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<GenerateKubernetesAgentInstallScriptCommand, GenerateKubernetesAgentInstallScriptResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("connection-status")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetConnectionStatusResponse))]
    public async Task<IActionResult> GetConnectionStatusAsync(
        [FromQuery] GetConnectionStatusRequest request, CancellationToken ct)
    {
        var response = await _mediator.RequestAsync<GetConnectionStatusRequest, GetConnectionStatusResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }
}
