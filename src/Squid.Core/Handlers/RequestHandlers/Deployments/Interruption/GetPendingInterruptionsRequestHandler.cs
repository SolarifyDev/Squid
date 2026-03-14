using System.Text.Json;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Message.Models.Deployments.Interruption;
using Squid.Message.Requests.Deployments.Interruption;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Interruption;

public class GetPendingInterruptionsRequestHandler : IRequestHandler<GetPendingInterruptionsRequest, GetPendingInterruptionsResponse>
{
    private readonly IDeploymentInterruptionService _interruptionService;

    public GetPendingInterruptionsRequestHandler(IDeploymentInterruptionService interruptionService)
    {
        _interruptionService = interruptionService;
    }

    public async Task<GetPendingInterruptionsResponse> Handle(IReceiveContext<GetPendingInterruptionsRequest> context, CancellationToken cancellationToken)
    {
        var interruptions = await _interruptionService.GetPendingInterruptionsAsync(context.Message.ServerTaskId, cancellationToken).ConfigureAwait(false);

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
            IsPending = i.Resolution == null,
            CreatedAt = i.CreatedAt
        }).ToList();

        return new GetPendingInterruptionsResponse { Interruptions = dtos };
    }
}
