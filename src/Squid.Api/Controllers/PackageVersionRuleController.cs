using Squid.Message.Commands.PackageVersioning;
using Squid.Message.Requests.PackageVersioning;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/package-version-rules")]
public class PackageVersionRuleController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(GetPackageVersionRuleResponse))]
    public async Task<IActionResult> GetPackageVersionRuleAsync([FromQuery] GetPackageVersionRuleRequest request)
    {
        var response = await mediator.RequestAsync<GetPackageVersionRuleRequest, GetPackageVersionRuleResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpGet("list")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(ListPackageVersionRulesResponse))]
    public async Task<IActionResult> ListPackageVersionRulesAsync([FromQuery] ListPackageVersionRulesRequest request)
    {
        var response = await mediator.RequestAsync<ListPackageVersionRulesRequest, ListPackageVersionRulesResponse>(request).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("set")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(SetPackageVersionRuleResponse))]
    public async Task<IActionResult> SetPackageVersionRuleAsync([FromBody] SetPackageVersionRuleCommand command)
    {
        var response = await mediator.SendAsync<SetPackageVersionRuleCommand, SetPackageVersionRuleResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }
}
