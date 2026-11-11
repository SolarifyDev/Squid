using Microsoft.AspNetCore.Authorization;
using Squid.Message.Commands.Deployments.Runbook;
using Squid.Message.Requests.Deployments.Runbook;

namespace Squid.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/runbooks")]
public class RunbookController : ControllerBase
{
    private readonly IMediator _mediator;

    public RunbookController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateRunbookResponse))]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateRunbookCommand command,
        CancellationToken cancellationToken)
    {
        var response = await _mediator
            .SendAsync<CreateRunbookCommand, CreateRunbookResponse>(command, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetRunbooksResponse))]
    public async Task<IActionResult> ListAsync(
        [FromQuery] GetRunbooksRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _mediator
            .RequestAsync<GetRunbooksRequest, GetRunbooksResponse>(request, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("detail/{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetRunbookResponse))]
    public async Task<IActionResult> DetailAsync(
        int id,
        [FromQuery] int? projectId,
        [FromQuery] int? spaceId,
        CancellationToken cancellationToken)
    {
        var request = new GetRunbookRequest
        {
            Id = id,
            ProjectId = projectId ?? 0,
            SpaceId = spaceId
        };
        var response = await _mediator
            .RequestAsync<GetRunbookRequest, GetRunbookResponse>(request, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateRunbookResponse))]
    public async Task<IActionResult> UpdateAsync(
        [FromBody] UpdateRunbookCommand command,
        CancellationToken cancellationToken)
    {
        var response = await _mediator
            .SendAsync<UpdateRunbookCommand, UpdateRunbookResponse>(command, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("copy")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CopyRunbookResponse))]
    public async Task<IActionResult> CopyAsync(
        [FromBody] CopyRunbookCommand command,
        CancellationToken cancellationToken)
    {
        var response = await _mediator
            .SendAsync<CopyRunbookCommand, CopyRunbookResponse>(command, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteRunbookResponse))]
    public async Task<IActionResult> DeleteAsync(
        [FromBody] DeleteRunbookCommand command,
        CancellationToken cancellationToken)
    {
        var response = await _mediator
            .SendAsync<DeleteRunbookCommand, DeleteRunbookResponse>(command, cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }
}
