using System.Text.Json;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Identity;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Teams;
using Squid.Message.Enums.Deployments;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Interruption;
using Squid.Message.Requests.Deployments.Interruption;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Interruption;

public class GetPendingInterruptionsRequestHandler(
    IDeploymentInterruptionService interruptionService,
    IInterruptionAuthorizationService interruptionAuthorization,
    ICurrentUser currentUser) : IRequestHandler<GetPendingInterruptionsRequest, GetPendingInterruptionsResponse>
{
    public async Task<GetPendingInterruptionsResponse> Handle(IReceiveContext<GetPendingInterruptionsRequest> context, CancellationToken cancellationToken)
    {
        var interruptions = await interruptionService.GetPendingInterruptionsAsync(context.Message.ServerTaskId, cancellationToken).ConfigureAwait(false);

        var dtos = new List<InterruptionDto>();

        foreach (var interruption in interruptions)
            dtos.Add(await ToDtoAsync(interruption, cancellationToken).ConfigureAwait(false));

        return new GetPendingInterruptionsResponse { Interruptions = dtos };
    }

    private async Task<InterruptionDto> ToDtoAsync(DeploymentInterruption interruption, CancellationToken ct)
    {
        var access = await ResolveAccessAsync(interruption, ct).ConfigureAwait(false);

        return new InterruptionDto
        {
            Id = interruption.Id,
            ServerTaskId = interruption.ServerTaskId,
            InterruptionType = interruption.InterruptionType,
            StepName = interruption.StepName,
            ActionName = interruption.ActionName,
            MachineName = interruption.MachineName,
            Form = interruption.FormJson != null ? JsonSerializer.Deserialize<InterruptionForm>(interruption.FormJson, SquidJsonDefaults.CaseInsensitive) : null,
            ResponsibleUserId = interruption.ResponsibleUserId,
            ResponsibleTeamIds = interruption.ResponsibleTeamIds,
            CanTakeResponsibility = access != InterruptionAccess.Denied,
            IsAdministratorOverride = access == InterruptionAccess.AdministratorOverride,
            IsPending = interruption.Resolution == null,
            CreatedDate = interruption.CreatedDate
        };
    }

    // Without a resolved user only team-less interruptions are actionable, exactly as submit would decide.
    private async Task<InterruptionAccess> ResolveAccessAsync(DeploymentInterruption interruption, CancellationToken ct)
    {
        if (currentUser.Id.HasValue)
            return await interruptionAuthorization.GetAccessAsync(interruption, currentUser.Id.Value, ct).ConfigureAwait(false);

        return TeamIdParser.ParseCsv(interruption.ResponsibleTeamIds).Count == 0 ? InterruptionAccess.Unrestricted : InterruptionAccess.Denied;
    }
}
