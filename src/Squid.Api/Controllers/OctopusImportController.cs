using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Requests.OctopusImport;

namespace Squid.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/octopus-import")]
public class OctopusImportController : ControllerBase
{
    private const long MaxUploadBytes = OctopusArchiveExtractionOptions.DefaultMaxTotalUncompressedSizeBytes;

    private readonly IMediator _mediator;

    public OctopusImportController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UploadOctopusImportResponse))]
    public async Task<IActionResult> UploadAsync(
        [FromForm] IFormFile file,
        [FromForm] int? spaceId,
        CancellationToken cancellationToken)
    {
        if (file == null)
        {
            return Ok(new UploadOctopusImportResponse
            {
                Code = System.Net.HttpStatusCode.BadRequest,
                Msg = "Octopus import upload requires a file."
            });
        }

        await using var stream = file.OpenReadStream();
        var response = await _mediator
            .SendAsync<UploadOctopusImportCommand, UploadOctopusImportResponse>(
                new UploadOctopusImportCommand
                {
                    SpaceId = spaceId,
                    FileName = file.FileName,
                    ContentType = file.ContentType,
                    SizeBytes = file.Length,
                    Content = stream
                },
                cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("{sessionId:guid}/extract")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ExtractOctopusImportResponse))]
    public async Task<IActionResult> ExtractAsync(
        Guid sessionId,
        [FromQuery] int? spaceId,
        CancellationToken cancellationToken)
    {
        var response = await _mediator
            .SendAsync<ExtractOctopusImportCommand, ExtractOctopusImportResponse>(
                new ExtractOctopusImportCommand
                {
                    SessionId = sessionId,
                    SpaceId = spaceId
                },
                cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("{sessionId:guid}/preview")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetOctopusImportPreviewResponse))]
    public async Task<IActionResult> PreviewAsync(
        Guid sessionId,
        [FromQuery] int? spaceId,
        CancellationToken cancellationToken)
    {
        var response = await _mediator
            .RequestAsync<GetOctopusImportPreviewRequest, GetOctopusImportPreviewResponse>(
                new GetOctopusImportPreviewRequest
                {
                    SessionId = sessionId,
                    SpaceId = spaceId
                },
                cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("{sessionId:guid}/validate")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ValidateOctopusImportResponse))]
    public async Task<IActionResult> ValidateAsync(
        Guid sessionId,
        [FromQuery] int? spaceId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ValidateOctopusImportCommand command,
        CancellationToken cancellationToken)
    {
        command ??= new ValidateOctopusImportCommand();
        command.SessionId = sessionId;
        command.SpaceId = spaceId ?? command.SpaceId;

        var response = await _mediator
            .SendAsync<ValidateOctopusImportCommand, ValidateOctopusImportResponse>(
                command,
                cancellationToken)
            .ConfigureAwait(false);

        return Ok(response);
    }
}
