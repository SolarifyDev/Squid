using System.Net;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class ConfirmOctopusImportCommandHandler(ICurrentUser currentUser, IOctopusImportConfirmationRunner confirmationRunner) : ICommandHandler<ConfirmOctopusImportCommand, ConfirmOctopusImportResponse>
{
    public async Task<ConfirmOctopusImportResponse> Handle(IReceiveContext<ConfirmOctopusImportCommand> context, CancellationToken cancellationToken)
    {
        var command = context.Message;
        var spaceId = GetSpaceId(command);
        var ownerUserId = GetOwnerUserId();

        var outcome = command.RunInBackground
            ? await confirmationRunner.EnqueueAsync(command.SessionId, spaceId, ownerUserId, cancellationToken).ConfigureAwait(false)
            : await confirmationRunner.RunAsync(command.SessionId, spaceId, ownerUserId, cancellationToken).ConfigureAwait(false);

        return ToResponse(outcome);
    }

    private static ConfirmOctopusImportResponse ToResponse(OctopusImportConfirmationOutcome outcome)
    {
        if (outcome.IsRejected)
            return new ConfirmOctopusImportResponse { Code = HttpStatusCode.BadRequest, Msg = outcome.Rejection.Message, Data = new ConfirmOctopusImportResponseData { Session = outcome.Session, BlockerSummary = OctopusImportBlockerSummaryBuilder.Build([outcome.Rejection]) } };

        return new ConfirmOctopusImportResponse { Code = HttpStatusCode.OK, Data = new ConfirmOctopusImportResponseData { Session = outcome.Session, BlockerSummary = OctopusImportBlockerSummaryBuilder.Build(outcome.Session?.Result) } };
    }

    private int GetOwnerUserId()
        => currentUser.Id ?? throw new UnauthorizedAccessException("Octopus import confirmation requires an authenticated user.");

    private static int GetSpaceId(ConfirmOctopusImportCommand command)
        => command.SpaceId ?? throw new UnauthorizedAccessException("Octopus import confirmation requires destination space context.");
}
