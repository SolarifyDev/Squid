using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Requests.Deployments.Machine;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Machine
{
    public class GetMachinesRequestHandler : IRequestHandler<GetMachinesRequest, PaginatedResponse<MachineDto>>
    {
        private readonly IMachineService _machineService;

        public GetMachinesRequestHandler(IMachineService machineService)
        {
            _machineService = machineService;
        }

        public async Task<PaginatedResponse<MachineDto>> Handle(IReceiveContext<GetMachinesRequest> context, CancellationToken cancellationToken)
        {
            return await _machineService.GetMachinesAsync(context.Message);
        }
    }
} 