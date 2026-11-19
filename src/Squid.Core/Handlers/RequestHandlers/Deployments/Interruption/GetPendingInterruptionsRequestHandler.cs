using System.Text.Json;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Identity;
using Squid.Core.Persistence.Entities.Deployments;
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

        var access = await interruptionAuthorization.GetAccessForEachAsync(interruptions, currentUser.Id, cancellationToken).ConfigureAwait(false);

        return new GetPendingInterruptionsResponse { Interruptions = interruptions.Select((interruption, i) => ToDto(interruption, access[i])).ToList() };
    }

    private static InterruptionDto ToDto(DeploymentInterruption interruption, InterruptionAccess access) => new()
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
