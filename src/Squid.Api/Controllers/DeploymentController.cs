using Microsoft.AspNetCore.Mvc;
using Squid.Message.Commands.Deployments.Deployment;

namespace Squid.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeploymentController : ControllerBase
{
    private readonly IMediator _mediator;

    public DeploymentController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(CreateDeploymentResponse))]
    public async Task<IActionResult> CreateDeploymentAsync([FromBody] CreateDeploymentCommand command)
    {
        var response = await _mediator.SendAsync<CreateDeploymentCommand, CreateDeploymentResponse>(command).ConfigureAwait(false);

        return Ok(response);
    }

    [HttpPost("validate")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(bool))]
    public async Task<IActionResult> ValidateDeploymentEnvironmentAsync([FromBody] ValidateDeploymentEnvironmentRequest request)
    {
        // 这里可以直接调用DeploymentService，或者创建一个专门的查询命令
        // 为了简化，我们返回一个占位符响应
        return Ok(new { IsValid = true, Message = "Environment validation endpoint - to be implemented" });
    }
}

public class ValidateDeploymentEnvironmentRequest
{
    public int ReleaseId { get; set; }
    public int EnvironmentId { get; set; }
}
