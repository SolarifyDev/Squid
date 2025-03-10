namespace Squid.Api.Controllers
{
    [ApiController]
    [Route("customers")]
    public class CustomerController : ControllerBase
    {
        private readonly IMediator _mediator;

        public CustomerController(IMediator mediator)
        {
            _mediator = mediator;
        }

        [HttpGet]
        [ProducesResponseType(typeof(PaginatedResponse<CustomerShortInfo>), 200)]
        public async Task<IActionResult> GetListAsync([FromQuery] GetCustomersRequest request)
        {
            var response =
                await _mediator.RequestAsync<GetCustomersRequest, PaginatedResponse<CustomerShortInfo>>(request);

            return Ok(response);
        }

        [HttpPost]
        [ProducesResponseType(typeof(CreateCustomerResponse), 200)]
        public async Task<IActionResult> CreateAsync([FromBody] CreateCustomerCommand command)
        {
            var response = await _mediator.SendAsync<CreateCustomerCommand, CreateCustomerResponse>(command);
            return CreatedAtAction("GetList", response);
        }

        [HttpPut("{customerId:guid}")]
        [ProducesResponseType(204)]
        public async Task<IActionResult> UpdateAsync(Guid customerId, [FromBody] UpdateCustomerModel model)
        {
            await _mediator.SendAsync(new UpdateCustomerCommand
            {
                CustomerId = customerId,
                Name = model.Name,
                Contact = model.Contact,
                Address = model.Address
            });

            return NoContent();
        }

        [HttpDelete]
        [ProducesResponseType(204)]
        public async Task<IActionResult> DeleteAsync([FromBody] DeleteCustomerCommand command)
        {
            await _mediator.SendAsync(command);
            return NoContent();
        }
    }
}