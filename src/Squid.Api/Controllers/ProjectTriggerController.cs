using Microsoft.AspNetCore.Authorization;
using Squid.Message.Commands.ProjectTriggers;
using Squid.Message.Requests.ProjectTriggers;

namespace Squid.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/project-triggers")]
public class ProjectTriggerController : ControllerBase
{
    private readonly IMediator _mediator;

    public ProjectTriggerController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateProjectTriggerResponse))]
    public async Task<IActionResult> CreateProjectTriggerAsync([FromBody] CreateProjectTriggerCommand command, CancellationToken cancellationToken)
    {
        var response = await _mediator.SendAsync<CreateProjectTriggerCommand, CreateProjectTriggerResponse>(command, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateProjectTriggerResponse))]
    public async Task<IActionResult> UpdateProjectTriggerAsync([FromBody] UpdateProjectTriggerCommand command, CancellationToken cancellationToken)
    {
        var response = await _mediator.SendAsync<UpdateProjectTriggerCommand, UpdateProjectTriggerResponse>(command, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("enable")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(EnableProjectTriggerResponse))]
    public async Task<IActionResult> EnableProjectTriggerAsync([FromBody] EnableProjectTriggerCommand command, CancellationToken cancellationToken)
    {
        var response = await _mediator.SendAsync<EnableProjectTriggerCommand, EnableProjectTriggerResponse>(command, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("disable")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DisableProjectTriggerResponse))]
    public async Task<IActionResult> DisableProjectTriggerAsync([FromBody] DisableProjectTriggerCommand command, CancellationToken cancellationToken)
    {
        var response = await _mediator.SendAsync<DisableProjectTriggerCommand, DisableProjectTriggerResponse>(command, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteProjectTriggerResponse))]
    public async Task<IActionResult> DeleteProjectTriggerAsync([FromBody] DeleteProjectTriggerCommand command, CancellationToken cancellationToken)
    {
        var response = await _mediator.SendAsync<DeleteProjectTriggerCommand, DeleteProjectTriggerResponse>(command, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetProjectTriggersResponse))]
    public async Task<IActionResult> GetProjectTriggersAsync([FromQuery] GetProjectTriggersRequest request, CancellationToken cancellationToken)
    {
        var response = await _mediator.RequestAsync<GetProjectTriggersRequest, GetProjectTriggersResponse>(request, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("detail")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetProjectTriggerResponse))]
    public async Task<IActionResult> GetProjectTriggerAsync([FromQuery] GetProjectTriggerRequest request, CancellationToken cancellationToken)
    {
        var response = await _mediator.RequestAsync<GetProjectTriggerRequest, GetProjectTriggerResponse>(request, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("history")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetProjectTriggerHistoryResponse))]
    public async Task<IActionResult> GetProjectTriggerHistoryAsync([FromQuery] GetProjectTriggerHistoryRequest request, CancellationToken cancellationToken)
    {
        var response = await _mediator.RequestAsync<GetProjectTriggerHistoryRequest, GetProjectTriggerHistoryResponse>(request, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("types")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetProjectTriggerTypesResponse))]
    public async Task<IActionResult> GetProjectTriggerTypesAsync([FromQuery] GetProjectTriggerTypesRequest request, CancellationToken cancellationToken)
    {
        var response = await _mediator.RequestAsync<GetProjectTriggerTypesRequest, GetProjectTriggerTypesResponse>(request, cancellationToken).ConfigureAwait(false);

        return Ok(response);
    }
}
