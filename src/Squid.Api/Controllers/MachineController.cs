using Squid.Core.Services.Machines;
using Squid.Message.Commands.Deployments.Machine;
using Squid.Message.Commands.Machine;
using Squid.Message.Requests.Deployments.Machine;
using Microsoft.AspNetCore.Authorization;

namespace Squid.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/machines")]
public class MachineController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IMachineInstallScriptService _installScriptService;
    private readonly IMachineRegistrationDataProvider _registrationDataProvider;

    public MachineController(
        IMediator mediator,
        IMachineInstallScriptService installScriptService,
        IMachineRegistrationDataProvider registrationDataProvider)
    {
        _mediator = mediator;
        _installScriptService = installScriptService;
        _registrationDataProvider = registrationDataProvider;
    }

    [HttpPost("register")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RegisterMachineResponse))]
    public async Task<IActionResult> RegisterAsync([FromBody] RegisterMachineCommand command, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(command.Thumbprint)) return BadRequest("Thumbprint is required");

        if (string.IsNullOrEmpty(command.SubscriptionId)) return BadRequest("SubscriptionId is required");

        var response = await _mediator.SendAsync<RegisterMachineCommand, RegisterMachineResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
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

    [HttpPost("generate-kubernetes-agent-install-script")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GenerateKubernetesAgentInstallScriptResponse))]
    public async Task<IActionResult> GenerateKubernetesAgentInstallScriptAsync(
        [FromBody] GenerateKubernetesAgentInstallScriptCommand command, CancellationToken ct)
    {
        var data = await _installScriptService.GenerateKubernetesAgentScriptAsync(command, User, ct).ConfigureAwait(false);

        return Ok(new GenerateKubernetesAgentInstallScriptResponse { Data = data });
    }

    [HttpGet("connection-status")]
    public async Task<IActionResult> GetConnectionStatusAsync(
        [FromQuery] string subscriptionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return BadRequest("subscriptionId is required");

        var exists = await _registrationDataProvider.ExistsBySubscriptionIdAsync(subscriptionId, ct).ConfigureAwait(false);

        return Ok(new { connected = exists });
    }
}
