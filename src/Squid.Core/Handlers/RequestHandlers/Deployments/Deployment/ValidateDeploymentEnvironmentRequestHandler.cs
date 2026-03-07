using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Validation;
using Squid.Message.Requests.Deployments.Deployment;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Deployment;

public class ValidateDeploymentEnvironmentRequestHandler : IRequestHandler<ValidateDeploymentEnvironmentRequest, ValidateDeploymentEnvironmentResponse>
{
    private readonly IDeploymentService _deploymentService;
    private readonly IDeploymentValidationOrchestrator _deploymentValidationOrchestrator;

    public ValidateDeploymentEnvironmentRequestHandler(
        IDeploymentService deploymentService,
        IDeploymentValidationOrchestrator deploymentValidationOrchestrator)
    {
        _deploymentService = deploymentService;
        _deploymentValidationOrchestrator = deploymentValidationOrchestrator;
    }

    public async Task<ValidateDeploymentEnvironmentResponse> Handle(IReceiveContext<ValidateDeploymentEnvironmentRequest> context, CancellationToken cancellationToken)
    {
        var specificMachineIds = NormalizeMachineIds(context.Message.SpecificMachineIds);
        var excludedMachineIds = NormalizeMachineIds(context.Message.ExcludedMachineIds);
        var skipActionIds = NormalizePositiveIds(context.Message.SkipActionIds);
        var queueTime = NormalizeUtc(context.Message.QueueTime);
        var queueTimeExpiry = NormalizeUtc(context.Message.QueueTimeExpiry);

        var validationContext = new DeploymentValidationContext
        {
            ReleaseId = context.Message.ReleaseId,
            EnvironmentId = context.Message.EnvironmentId,
            QueueTime = queueTime,
            QueueTimeExpiry = queueTimeExpiry,
            SpecificMachineIds = specificMachineIds,
            ExcludedMachineIds = excludedMachineIds,
            SkipActionIds = skipActionIds
        };

        var environmentValidation = await _deploymentService
            .ValidateDeploymentEnvironmentAsync(validationContext, cancellationToken).ConfigureAwait(false);

        var report = await _deploymentValidationOrchestrator
            .ValidateAsync(DeploymentValidationStage.Precheck, validationContext, cancellationToken).ConfigureAwait(false);

        var reasons = environmentValidation.Reasons
            .Concat(report.Issues.Where(i => i.IsBlocking).Select(i => i.Message))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ValidateDeploymentEnvironmentResponse
        {
            IsValid = environmentValidation.IsValid && report.IsValid,
            Reasons = reasons,
            AvailableMachineCount = environmentValidation.AvailableMachineCount,
            LifecycleId = environmentValidation.LifecycleId,
            AllowedEnvironmentIds = environmentValidation.AllowedEnvironmentIds.ToList()
        };
    }

    private static HashSet<int> NormalizeMachineIds(IEnumerable<string> machineIds)
    {
        if (machineIds == null)
            return new HashSet<int>();

        var ids = new HashSet<int>();

        foreach (var raw in machineIds)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (int.TryParse(raw.Trim(), out var parsed) && parsed > 0)
                ids.Add(parsed);
        }

        return ids;
    }

    private static HashSet<int> NormalizePositiveIds(IEnumerable<int> ids)
    {
        if (ids == null)
            return new HashSet<int>();

        return ids.Where(id => id > 0).ToHashSet();
    }

    private static DateTimeOffset? NormalizeUtc(DateTimeOffset? value)
    {
        if (!value.HasValue)
            return null;

        var dateTime = value.Value;

        return dateTime.Offset == TimeSpan.Zero
            ? dateTime
            : dateTime.ToUniversalTime();
    }
}
