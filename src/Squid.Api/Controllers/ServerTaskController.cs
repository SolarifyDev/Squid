using Squid.Message.Commands.Deployments.ServerTask;
using Squid.Message.Requests.Deployments.ServerTask;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/tasks")]
public class ServerTaskController : ControllerBase
{
    private readonly IMediator _mediator;

    public ServerTaskController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("{taskId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetServerTaskResponse))]
    public async Task<IActionResult> GetTaskAsync(int taskId)
    {
        var response = await _mediator.RequestAsync<GetServerTaskRequest, GetServerTaskResponse>(
            new GetServerTaskRequest { TaskId = taskId }).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{taskId:int}/details")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetServerTaskDetailsResponse))]
    public async Task<IActionResult> GetTaskDetailsAsync(int taskId, [FromQuery] bool? verbose = null, [FromQuery] int? tail = null)
    {
        var request = new GetServerTaskDetailsRequest
        {
            TaskId = taskId,
            Verbose = verbose,
            Tail = tail
        };

        var response = await _mediator
            .RequestAsync<GetServerTaskDetailsRequest, GetServerTaskDetailsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{taskId:int}/logs")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetServerTaskLogsResponse))]
    public async Task<IActionResult> GetTaskLogsAsync(int taskId, [FromQuery] long? afterSequenceNumber = null, [FromQuery] int? take = null)
    {
        var request = new GetServerTaskLogsRequest
        {
            TaskId = taskId,
            AfterSequenceNumber = afterSequenceNumber,
            Take = take
        };

        var response = await _mediator.RequestAsync<GetServerTaskLogsRequest, GetServerTaskLogsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{taskId:int}/nodes/{nodeId:long}/logs")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetServerTaskNodeLogsResponse))]
    public async Task<IActionResult> GetTaskNodeLogsAsync(int taskId, long nodeId, [FromQuery] long? afterSequenceNumber = null, [FromQuery] int? take = null)
    {
        var request = new GetServerTaskNodeLogsRequest
        {
            TaskId = taskId,
            NodeId = nodeId,
            AfterSequenceNumber = afterSequenceNumber,
            Take = take
        };

        var response = await _mediator
            .RequestAsync<GetServerTaskNodeLogsRequest, GetServerTaskNodeLogsResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("{taskId:int}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CancelServerTaskResponse))]
    public async Task<IActionResult> CancelTaskAsync(int taskId)
    {
        var response = await _mediator.SendAsync<CancelServerTaskCommand, CancelServerTaskResponse>(
            new CancelServerTaskCommand { TaskId = taskId }).ConfigureAwait(false);

        return Ok(response);
    }
}
