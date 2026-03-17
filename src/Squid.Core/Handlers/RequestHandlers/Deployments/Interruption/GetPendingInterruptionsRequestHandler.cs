using System.Text.Json;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Teams;
using Squid.Message.Models.Deployments.Interruption;
using Squid.Message.Requests.Deployments.Interruption;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Interruption;

public class GetPendingInterruptionsRequestHandler(
    IDeploymentInterruptionService interruptionService,
    ITeamDataProvider teamDataProvider,
    ICurrentUser currentUser) : IRequestHandler<GetPendingInterruptionsRequest, GetPendingInterruptionsResponse>
{
    public async Task<GetPendingInterruptionsResponse> Handle(IReceiveContext<GetPendingInterruptionsRequest> context, CancellationToken cancellationToken)
    {
        var interruptions = await interruptionService.GetPendingInterruptionsAsync(context.Message.ServerTaskId, cancellationToken).ConfigureAwait(false);

        var userTeamIds = await LoadCurrentUserTeamIdsAsync(cancellationToken).ConfigureAwait(false);

        var dtos = interruptions.Select(i => new InterruptionDto
        {
            Id = i.Id,
            ServerTaskId = i.ServerTaskId,
            InterruptionType = i.InterruptionType,
            StepName = i.StepName,
            ActionName = i.ActionName,
            MachineName = i.MachineName,
            Form = i.FormJson != null ? JsonSerializer.Deserialize<InterruptionForm>(i.FormJson) : null,
            ResponsibleUserId = i.ResponsibleUserId,
            ResponsibleTeamIds = i.ResponsibleTeamIds,
            CanTakeResponsibility = CanUserAct(i.ResponsibleTeamIds, userTeamIds),
            IsPending = i.Resolution == null,
            CreatedDate = i.CreatedDate
        }).ToList();

        return new GetPendingInterruptionsResponse { Interruptions = dtos };
    }

    private async Task<HashSet<int>> LoadCurrentUserTeamIdsAsync(CancellationToken ct)
    {
        if (!currentUser.Id.HasValue) return new();

        var ids = await teamDataProvider.GetTeamIdsByUserIdAsync(currentUser.Id.Value, ct).ConfigureAwait(false);

        return ids.ToHashSet();
    }

    private static bool CanUserAct(string responsibleTeamIds, HashSet<int> userTeamIds)
    {
        var requiredTeamIds = TeamIdParser.ParseCsv(responsibleTeamIds);
        if (requiredTeamIds.Count == 0) return true;
        if (userTeamIds.Count == 0) return false;

        return requiredTeamIds.Any(userTeamIds.Contains);
    }
}
