using System.Net;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class SelectOctopusImportProjectsCommandHandler(IOctopusImportProjectSelectionService projectSelection) : ICommandHandler<SelectOctopusImportProjectsCommand, SelectOctopusImportProjectsResponse>
{
    public async Task<SelectOctopusImportProjectsResponse> Handle(IReceiveContext<SelectOctopusImportProjectsCommand> context, CancellationToken cancellationToken)
    {
        var command = context.Message;
        var spaceId = command.SpaceId ?? throw new UnauthorizedAccessException("Octopus import requires destination space context.");

        var session = await projectSelection.SelectProjectsAsync(command.SessionId, spaceId, command.ProjectIds, cancellationToken).ConfigureAwait(false);

        return new SelectOctopusImportProjectsResponse { Code = HttpStatusCode.OK, Data = session };
    }
}
