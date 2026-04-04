using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Requests.Deployments.Release;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/releases")]
public class ReleaseController : ControllerBase
{
    private readonly IMediator _mediator;

    public ReleaseController(IMediator mediator)
    {
        _mediator = mediator;
    }
    
    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateReleaseResponse))]
    public async Task<IActionResult> CreateReleaseAsync([FromBody] CreateReleaseCommand command)
    {
        var response = await _mediator.SendAsync<CreateReleaseCommand, CreateReleaseResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateReleaseResponse))]
    public async Task<IActionResult> UpdateReleaseAsync([FromBody] UpdateReleaseCommand command)
    {
        var response = await _mediator.SendAsync<UpdateReleaseCommand, UpdateReleaseResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpPost("update-variables")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateReleaseVariableAsync([FromBody] UpdateReleaseVariableCommand command)
    {
        await _mediator.SendAsync(command).ConfigureAwait(false);

        return Ok();
    }
    
    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteReleaseResponse))]
    public async Task<IActionResult> DeleteReleaseAsync([FromBody] DeleteReleaseCommand command)
    {
        var response = await _mediator.SendAsync<DeleteReleaseCommand, DeleteReleaseResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
    
    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetReleasesResponse))]
    public async Task<IActionResult> GetReleasesAsync([FromQuery] GetReleasesRequest request)
    {
        var response = await _mediator.RequestAsync<GetReleasesRequest, GetReleasesResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{releaseId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetReleaseDetailResponse))]
    public async Task<IActionResult> GetReleaseDetailAsync(int releaseId)
    {
        var request = new GetReleaseDetailRequest { ReleaseId = releaseId };
        var response = await _mediator.RequestAsync<GetReleaseDetailRequest, GetReleaseDetailResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{releaseId:int}/variable-snapshot")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetReleaseVariableSnapshotResponse))]
    public async Task<IActionResult> GetReleaseVariableSnapshotAsync(int releaseId)
    {
        var request = new GetReleaseVariableSnapshotRequest { ReleaseId = releaseId };
        var response = await _mediator.RequestAsync<GetReleaseVariableSnapshotRequest, GetReleaseVariableSnapshotResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{releaseId:int}/progression")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetReleaseProgressionResponse))]
    public async Task<IActionResult> GetReleaseProgressionAsync(int releaseId)
    {
        var request = new GetReleaseProgressionRequest { ReleaseId = releaseId };
        var response = await _mediator.RequestAsync<GetReleaseProgressionRequest, GetReleaseProgressionResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}