using System.Net;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Commands.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class ConnectOctopusImportCommandHandler(IOctopusImportServerSourceService serverSource) : ICommandHandler<ConnectOctopusImportCommand, ConnectOctopusImportResponse>
{
    public async Task<ConnectOctopusImportResponse> Handle(IReceiveContext<ConnectOctopusImportCommand> context, CancellationToken cancellationToken)
    {
        var command = context.Message;
        var spaceId = command.SpaceId ?? throw new UnauthorizedAccessException("Octopus import requires destination space context.");

        var session = await serverSource.ConnectAsync(spaceId, ConnectionOf(command), cancellationToken).ConfigureAwait(false);

        return new ConnectOctopusImportResponse { Code = HttpStatusCode.OK, Data = new ConnectOctopusImportResponseData { Session = session } };
    }

    // A client that sends no server yet connects unchecked; its first fetch checks the key instead.
    private static OctopusServerConnection ConnectionOf(ConnectOctopusImportCommand command)
        => string.IsNullOrWhiteSpace(command.ServerUrl) && string.IsNullOrWhiteSpace(command.ApiKey) ? null : new OctopusServerConnection(command.ServerUrl, command.ApiKey, command.OctopusSpaceId);
}
