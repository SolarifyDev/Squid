using Squid.Core.Services.Machines;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Handlers.RequestHandlers.Machines;

/// <summary>
/// Mediator bridge for the connection-log endpoint. Clamps the requested entry
/// cap to a sane range and delegates to <see cref="IMachineConnectionLogReader"/>,
/// which resolves the machine's Halibut endpoint URI and reads the real
/// per-endpoint connection events the server runtime recorded.
/// </summary>
public sealed class GetMachineConnectionLogRequestHandler : IRequestHandler<GetMachineConnectionLogRequest, GetMachineConnectionLogResponse>
{
    private readonly IMachineConnectionLogReader _connectionLogReader;

    public GetMachineConnectionLogRequestHandler(IMachineConnectionLogReader connectionLogReader)
    {
        _connectionLogReader = connectionLogReader;
    }

    public async Task<GetMachineConnectionLogResponse> Handle(IReceiveContext<GetMachineConnectionLogRequest> context, CancellationToken cancellationToken)
    {
        var maxEntries = ClampMaxEntries(context.Message.MaxEntries);

        var data = await _connectionLogReader.ReadAsync(context.Message.MachineId, maxEntries, cancellationToken).ConfigureAwait(false);

        return new GetMachineConnectionLogResponse { Data = data };
    }

    private static int ClampMaxEntries(int? requested)
    {
        var value = requested ?? GetMachineConnectionLogRequest.DefaultMaxEntries;

        if (value < 1) return 1;

        return value > GetMachineConnectionLogRequest.MaxAllowedEntries ? GetMachineConnectionLogRequest.MaxAllowedEntries : value;
    }
}
