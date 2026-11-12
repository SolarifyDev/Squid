using System.Net;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class ProbeOctopusServerCommandHandler(IOctopusImportServerSourceService serverSource) : ICommandHandler<ProbeOctopusServerCommand, ProbeOctopusServerResponse>
{
    public async Task<ProbeOctopusServerResponse> Handle(IReceiveContext<ProbeOctopusServerCommand> context, CancellationToken cancellationToken)
    {
        var probe = await serverSource.ProbeAsync(context.Message.ServerUrl, context.Message.ApiKey, cancellationToken).ConfigureAwait(false);

        return new ProbeOctopusServerResponse { Code = HttpStatusCode.OK, Data = probe };
    }
}
