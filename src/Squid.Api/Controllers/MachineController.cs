using Microsoft.AspNetCore.Mvc;
using Squid.Message.Commands.Deployments.Machine;
using Squid.Message.Requests.Deployments.Machine;

namespace Squid.Api.Controllers
{
    [ApiController]
    [Route("api/machine")]
    public class MachineController : ControllerBase 
    { 
        private readonly IMediator _mediator; 

        public MachineController(IMediator mediator) 
        { 
            _mediator = mediator; 
        } 

        [HttpGet("list")]
        public async Task<IActionResult> GetList([FromQuery] GetMachinesRequest request) 
        { 
            var response = await _mediator.Send(request); 
            return Ok(SquidResponse.Success(response)); 
        } 

        [HttpPost("create")]
        public async Task<IActionResult> Create([FromBody] CreateMachineCommand command) 
        { 
            var response = await _mediator.Send(command); 
            return Ok(SquidResponse.Success(response)); 
        } 

        [HttpPost("update")]
        public async Task<IActionResult> Update([FromBody] UpdateMachineCommand command) 
        { 
            var response = await _mediator.Send(command); 
            return Ok(SquidResponse.Success(response)); 
        } 

        [HttpPost("delete")]
        public async Task<IActionResult> Delete([FromBody] DeleteMachineCommand command) 
        { 
            var response = await _mediator.Send(command); 
            return Ok(SquidResponse.Success(response)); 
        } 
    } 
} 