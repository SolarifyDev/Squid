using Squid.Message.Commands.Deployments.Account;
using Squid.Message.Requests.Deployments.Account;
using Microsoft.AspNetCore.Authorization;

namespace Squid.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/deployment-accounts")]
public class DeploymentAccountController : ControllerBase
{
    private readonly IMediator _mediator;

    public DeploymentAccountController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateDeploymentAccountResponse))]
    public async Task<IActionResult> CreateDeploymentAccountAsync([FromBody] CreateDeploymentAccountCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<CreateDeploymentAccountCommand, CreateDeploymentAccountResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateDeploymentAccountResponse))]
    public async Task<IActionResult> UpdateDeploymentAccountAsync([FromBody] UpdateDeploymentAccountCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<UpdateDeploymentAccountCommand, UpdateDeploymentAccountResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteDeploymentAccountsResponse))]
    public async Task<IActionResult> DeleteDeploymentAccountsAsync([FromBody] DeleteDeploymentAccountsCommand command, CancellationToken ct)
    {
        var response = await _mediator.SendAsync<DeleteDeploymentAccountsCommand, DeleteDeploymentAccountsResponse>(command, ct).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetDeploymentAccountsResponse))]
    public async Task<IActionResult> GetDeploymentAccountsAsync([FromQuery] GetDeploymentAccountsRequest request, CancellationToken ct)
    {
        var response = await _mediator.RequestAsync<GetDeploymentAccountsRequest, GetDeploymentAccountsResponse>(request, ct).ConfigureAwait(false);

        return Ok(response);
    }
}
