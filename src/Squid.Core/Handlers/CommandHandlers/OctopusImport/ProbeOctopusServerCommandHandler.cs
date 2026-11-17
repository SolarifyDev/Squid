using System.Net;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Commands.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class ProbeOctopusServerCommandHandler(IOctopusImportServerSourceService serverSource) : ICommandHandler<ProbeOctopusServerCommand, ProbeOctopusServerResponse>
{
    public async Task<ProbeOctopusServerResponse> Handle(IReceiveContext<ProbeOctopusServerCommand> context, CancellationToken cancellationToken)
    {
        var command = context.Message;
        var probe = await serverSource.ProbeAsync(new OctopusServerConnection(command.ServerUrl, command.ApiKey, command.OctopusSpaceId), cancellationToken).ConfigureAwait(false);

        return new ProbeOctopusServerResponse { Code = HttpStatusCode.OK, Data = probe };
    }
}
