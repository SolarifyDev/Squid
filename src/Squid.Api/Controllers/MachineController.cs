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
        var response = await _mediator.SendAsync<RegisterKubernetesAgentCommand, RegisterMachineResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("register/kubernetes-api")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RegisterMachineResponse))]
    public async Task<IActionResult> RegisterKubernetesApiAsync([FromBody] RegisterKubernetesApiCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<RegisterKubernetesApiCommand, RegisterMachineResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("register/openclaw")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RegisterMachineResponse))]
    public async Task<IActionResult> RegisterOpenClawAsync([FromBody] RegisterOpenClawCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<RegisterOpenClawCommand, RegisterMachineResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("register/ssh")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RegisterMachineResponse))]
    public async Task<IActionResult> RegisterSshAsync([FromBody] RegisterSshCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<RegisterSshCommand, RegisterMachineResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("register/tentacle-polling")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RegisterMachineResponse))]
    public async Task<IActionResult> RegisterTentaclePollingAsync([FromBody] RegisterTentaclePollingCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<RegisterTentaclePollingCommand, RegisterMachineResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("generate-kubernetes-agent-install-script")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GenerateKubernetesAgentInstallScriptResponse))]
    public async Task<IActionResult> GenerateKubernetesAgentInstallScriptAsync([FromBody] GenerateKubernetesAgentInstallScriptCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<GenerateKubernetesAgentInstallScriptCommand, GenerateKubernetesAgentInstallScriptResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("generate-kubernetes-agent-upgrade-script")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GenerateKubernetesAgentUpgradeScriptResponse))]
    public async Task<IActionResult> GenerateKubernetesAgentUpgradeScriptAsync([FromBody] GenerateKubernetesAgentUpgradeScriptCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<GenerateKubernetesAgentUpgradeScriptCommand, GenerateKubernetesAgentUpgradeScriptResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateMachineResponse))]
    public async Task<IActionResult> UpdateMachineAsync([FromBody] UpdateMachineCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<UpdateMachineCommand, UpdateMachineResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetMachinesResponse))]
    public async Task<IActionResult> GetMachinesAsync([FromQuery] GetMachinesRequest request, CancellationToken ct)
    {
        var response = await _mediator.RequestAsync<GetMachinesRequest, GetMachinesResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteMachinesResponse))]
    public async Task<IActionResult> DeleteMachinesAsync([FromBody] DeleteMachinesCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<DeleteMachinesCommand, DeleteMachinesResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("latest-agent-version")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetLatestKubernetesAgentVersionResponse))]
    public async Task<IActionResult> GetLatestKubernetesAgentVersionAsync(CancellationToken ct)
    {
        var response = await _mediator.RequestAsync<GetLatestKubernetesAgentVersionRequest, GetLatestKubernetesAgentVersionResponse>(new GetLatestKubernetesAgentVersionRequest(), ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("connection-status")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetConnectionStatusResponse))]
    public async Task<IActionResult> GetConnectionStatusAsync([FromQuery] GetConnectionStatusRequest request, CancellationToken ct)
    {
        var response = await _mediator.RequestAsync<GetConnectionStatusRequest, GetConnectionStatusResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("{machineId:int}/health-check")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(RunMachineHealthCheckResponse))]
    public async Task<IActionResult> RunHealthCheckAsync(int machineId, CancellationToken ct)
    {
        var command = new RunMachineHealthCheckCommand { MachineId = machineId };
        var response = await _mediator.SendAsync<RunMachineHealthCheckCommand, RunMachineHealthCheckResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }
}
