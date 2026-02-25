using Squid.Message.Commands.Deployments.Certificate;
using Squid.Message.Requests.Deployments.Certificate;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/certificates")]
public class CertificateController : ControllerBase
{
    private readonly IMediator _mediator;

    public CertificateController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("create")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateCertificateResponse))]
    public async Task<IActionResult> CreateCertificateAsync([FromBody] CreateCertificateCommand command)
    {
        var response = await _mediator.SendAsync<CreateCertificateCommand, CreateCertificateResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("update")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(UpdateCertificateResponse))]
    public async Task<IActionResult> UpdateCertificateAsync([FromBody] UpdateCertificateCommand command)
    {
        var response = await _mediator.SendAsync<UpdateCertificateCommand, UpdateCertificateResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("replace")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ReplaceCertificateResponse))]
    public async Task<IActionResult> ReplaceCertificateAsync([FromBody] ReplaceCertificateCommand command)
    {
        var response = await _mediator.SendAsync<ReplaceCertificateCommand, ReplaceCertificateResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("delete")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(DeleteCertificatesResponse))]
    public async Task<IActionResult> DeleteCertificatesAsync([FromBody] DeleteCertificatesCommand command)
    {
        var response = await _mediator.SendAsync<DeleteCertificatesCommand, DeleteCertificatesResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetCertificatesResponse))]
    public async Task<IActionResult> GetCertificatesAsync([FromQuery] GetCertificatesRequest request)
    {
        var response = await _mediator.RequestAsync<GetCertificatesRequest, GetCertificatesResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }
}
