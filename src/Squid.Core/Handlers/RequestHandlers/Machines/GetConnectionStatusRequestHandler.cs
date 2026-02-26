using Squid.Core.Services.Machines;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Handlers.RequestHandlers.Machines;

public class GetConnectionStatusRequestHandler : IRequestHandler<GetConnectionStatusRequest, GetConnectionStatusResponse>
{
    private readonly IMachineDataProvider _machineDataProvider;

    public GetConnectionStatusRequestHandler(IMachineDataProvider machineDataProvider)
    {
        _machineDataProvider = machineDataProvider;
    }

    public async Task<GetConnectionStatusResponse> Handle(
        IReceiveContext<GetConnectionStatusRequest> context, CancellationToken cancellationToken)
    {
        var exists = await _machineDataProvider.ExistsBySubscriptionIdAsync(
            context.Message.SubscriptionId, cancellationToken).ConfigureAwait(false);

        return new GetConnectionStatusResponse
        {
            Data = new GetConnectionStatusResponseData { Connected = exists }
        };
    }
}
