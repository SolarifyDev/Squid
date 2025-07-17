using Squid.Core.Services.Deployments.Machine;
using Squid.Message.Requests.Deployments.Machine;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Machine;

public class GetMachinesRequestHandler : IRequestHandler<GetMachinesRequest, GetMachinesResponse>
{
    private readonly IMachineService _machineService;

    public GetMachinesRequestHandler(IMachineService machineService)
    {
        _machineService = machineService;
    }

    public async Task<GetMachinesResponse> Handle(IReceiveContext<GetMachinesRequest> context, CancellationToken cancellationToken)
    {
        return await _machineService.GetMachinesAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
} 