using System.Net;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class ConnectOctopusImportCommandHandler(IOctopusImportServerSourceService serverSource) : ICommandHandler<ConnectOctopusImportCommand, ConnectOctopusImportResponse>
{
    public async Task<ConnectOctopusImportResponse> Handle(IReceiveContext<ConnectOctopusImportCommand> context, CancellationToken cancellationToken)
    {
        var spaceId = context.Message.SpaceId ?? throw new UnauthorizedAccessException("Octopus import requires destination space context.");

        var session = await serverSource.ConnectAsync(spaceId, cancellationToken).ConfigureAwait(false);

        return new ConnectOctopusImportResponse { Code = HttpStatusCode.OK, Data = new ConnectOctopusImportResponseData { Session = session } };
    }
}
