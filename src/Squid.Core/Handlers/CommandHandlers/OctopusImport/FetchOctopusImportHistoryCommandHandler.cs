using System.Net;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class FetchOctopusImportHistoryCommandHandler(IOctopusImportServerSourceService serverSource) : ICommandHandler<FetchOctopusImportHistoryCommand, FetchOctopusImportHistoryResponse>
{
    public async Task<FetchOctopusImportHistoryResponse> Handle(IReceiveContext<FetchOctopusImportHistoryCommand> context, CancellationToken cancellationToken)
    {
        var command = context.Message;
        var spaceId = command.SpaceId ?? throw new UnauthorizedAccessException("Octopus import requires destination space context.");

        var progress = await serverSource.FetchHistoryAsync(new OctopusHistoryFetchRequest(command.SessionId, spaceId, command.ServerUrl, command.ApiKey, command.OctopusSpaceId), cancellationToken).ConfigureAwait(false);

        return new FetchOctopusImportHistoryResponse { Code = HttpStatusCode.OK, Data = progress };
    }
}
