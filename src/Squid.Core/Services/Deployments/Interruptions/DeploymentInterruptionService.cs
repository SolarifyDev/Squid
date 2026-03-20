using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Identity;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Interruption;

namespace Squid.Core.Services.Deployments.Interruptions;

public class CreateInterruptionRequest
{
    public int ServerTaskId { get; set; }
    public int DeploymentId { get; set; }
    public InterruptionType InterruptionType { get; set; }
    public int StepDisplayOrder { get; set; }
    public string StepName { get; set; }
    public string ActionName { get; set; }
    public string MachineName { get; set; }
    public string ErrorMessage { get; set; }
    public InterruptionForm Form { get; set; }
    public int SpaceId { get; set; }
    public string ResponsibleTeamIds { get; set; }
}

public interface IDeploymentInterruptionService : IScopedDependency
{
    Task<DeploymentInterruption> CreateInterruptionAsync(CreateInterruptionRequest request, CancellationToken ct = default);

    Task SubmitInterruptionAsync(int interruptionId, Dictionary<string, string> values, CancellationToken ct = default);

    Task TakeResponsibilityAsync(int interruptionId, string userId, CancellationToken ct = default);

    Task<InterruptionOutcome> WaitForInterruptionAsync(int interruptionId, CancellationToken ct);

    Task<DeploymentInterruption> GetInterruptionByIdAsync(int interruptionId, CancellationToken ct = default);

    Task<List<DeploymentInterruption>> GetPendingInterruptionsAsync(int serverTaskId, CancellationToken ct = default);

    Task<DeploymentInterruption> FindResolvedInterruptionAsync(int serverTaskId, string stepName, string actionName, string machineName, CancellationToken ct = default);

    Task CancelPendingInterruptionsAsync(int serverTaskId, CancellationToken ct = default);
}

public class DeploymentInterruptionService(IRepository repository, IUnitOfWork unitOfWork, IServerTaskService serverTaskService, IInterruptionAuthorizationService authService, ICurrentUser currentUser) : IDeploymentInterruptionService
{
    private const int PollIntervalMs = 5000;

    public async Task<DeploymentInterruption> CreateInterruptionAsync(CreateInterruptionRequest request, CancellationToken ct = default)
    {
        var interruption = new DeploymentInterruption
        {
            ServerTaskId = request.ServerTaskId,
            DeploymentId = request.DeploymentId,
            InterruptionType = request.InterruptionType,
            StepDisplayOrder = request.StepDisplayOrder,
            StepName = request.StepName,
            ActionName = request.ActionName,
            MachineName = request.MachineName,
            ErrorMessage = request.ErrorMessage,
            FormJson = request.Form != null ? JsonSerializer.Serialize(request.Form) : null,
            SpaceId = request.SpaceId,
            ResponsibleTeamIds = request.ResponsibleTeamIds
        };

        await repository.InsertAsync(interruption, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        await serverTaskService.SetHasPendingInterruptionsAsync(request.ServerTaskId, true, ct).ConfigureAwait(false);

        return interruption;
    }

    public async Task SubmitInterruptionAsync(int interruptionId, Dictionary<string, string> values, CancellationToken ct = default)
    {
        var interruption = await repository.GetByIdAsync<DeploymentInterruption>(interruptionId, ct).ConfigureAwait(false);

        if (interruption == null)
            throw new InvalidOperationException($"DeploymentInterruption {interruptionId} not found");

        if (currentUser.Id.HasValue)
            await authService.EnsureCanActAsync(interruption, currentUser.Id.Value, ct).ConfigureAwait(false);

        var outcome = InterruptionFormBuilder.ResolveOutcome(interruption.InterruptionType, values);

        interruption.SubmittedValuesJson = JsonSerializer.Serialize(values);
        interruption.Resolution = outcome.ToString();
        interruption.ResolvedAt = DateTimeOffset.UtcNow;

        await repository.UpdateAsync(interruption, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        var remaining = await GetPendingInterruptionsAsync(interruption.ServerTaskId, ct).ConfigureAwait(false);

        if (remaining.Count == 0)
            await serverTaskService.SetHasPendingInterruptionsAsync(interruption.ServerTaskId, false, ct).ConfigureAwait(false);
    }

    public async Task TakeResponsibilityAsync(int interruptionId, string userId, CancellationToken ct = default)
    {
        var interruption = await repository.GetByIdAsync<DeploymentInterruption>(interruptionId, ct).ConfigureAwait(false);

        if (interruption == null)
            throw new InvalidOperationException($"DeploymentInterruption {interruptionId} not found");

        if (int.TryParse(userId, out var uid))
            await authService.EnsureCanActAsync(interruption, uid, ct).ConfigureAwait(false);

        interruption.ResponsibleUserId = userId;

        await repository.UpdateAsync(interruption, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<InterruptionOutcome> WaitForInterruptionAsync(int interruptionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var interruption = await repository.QueryNoTracking<DeploymentInterruption>(i => i.Id == interruptionId).FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (interruption?.Resolution != null && Enum.TryParse<InterruptionOutcome>(interruption.Resolution, true, out var outcome))
                return outcome;

            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        return InterruptionOutcome.Abort;
    }

    public async Task<DeploymentInterruption> GetInterruptionByIdAsync(int interruptionId, CancellationToken ct = default)
    {
        return await repository.GetByIdAsync<DeploymentInterruption>(interruptionId, ct).ConfigureAwait(false);
    }

    public async Task<List<DeploymentInterruption>> GetPendingInterruptionsAsync(int serverTaskId, CancellationToken ct = default)
    {
        return await repository.ToListAsync<DeploymentInterruption>(i => i.ServerTaskId == serverTaskId && i.Resolution == null, ct).ConfigureAwait(false);
    }

    public async Task<DeploymentInterruption> FindResolvedInterruptionAsync(int serverTaskId, string stepName, string actionName, string machineName, CancellationToken ct = default)
    {
        return await repository.QueryNoTracking<DeploymentInterruption>(i =>
                i.ServerTaskId == serverTaskId && i.StepName == stepName && i.ActionName == actionName && i.MachineName == machineName && i.Resolution != null)
            .OrderByDescending(i => i.CreatedDate)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task CancelPendingInterruptionsAsync(int serverTaskId, CancellationToken ct = default)
    {
        var pending = await GetPendingInterruptionsAsync(serverTaskId, ct).ConfigureAwait(false);

        foreach (var interruption in pending)
        {
            interruption.Resolution = InterruptionOutcome.Abort.ToString();
            interruption.ResolvedAt = DateTimeOffset.UtcNow;
            await repository.UpdateAsync(interruption, ct).ConfigureAwait(false);
        }

        if (pending.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            await serverTaskService.SetHasPendingInterruptionsAsync(serverTaskId, false, ct).ConfigureAwait(false);
        }
    }
}
