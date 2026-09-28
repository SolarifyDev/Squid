using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping.Actions;

public interface IOctopusImportRuntimeActionHandlerValidator : IScopedDependency
{
    IReadOnlyList<OctopusImportDiagnosticDto> Validate(
        OctopusDeploymentActionDto sourceAction,
        CreateOrUpdateDeploymentActionModel mappedAction);
}

public sealed class OctopusImportRuntimeActionHandlerValidator : IOctopusImportRuntimeActionHandlerValidator
{
    private readonly IActionHandlerRegistry _runtimeActionHandlerRegistry;

    public OctopusImportRuntimeActionHandlerValidator(IActionHandlerRegistry runtimeActionHandlerRegistry)
    {
        _runtimeActionHandlerRegistry = runtimeActionHandlerRegistry
            ?? throw new ArgumentNullException(nameof(runtimeActionHandlerRegistry));
    }

    public IReadOnlyList<OctopusImportDiagnosticDto> Validate(
        OctopusDeploymentActionDto sourceAction,
        CreateOrUpdateDeploymentActionModel mappedAction)
    {
        ArgumentNullException.ThrowIfNull(sourceAction);

        if (mappedAction == null || mappedAction.IsDisabled)
            return [];

        var runtimeAction = ToRuntimeAction(mappedAction);
        if (_runtimeActionHandlerRegistry.Resolve(runtimeAction) != null)
            return [];

        return
        [
            OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
            {
                Severity = OctopusImportCompatibilitySeverity.Blocker,
                Code = OctopusImportActionMappingDiagnosticCodes.MissingRuntimeActionHandler,
                Message = $"Mapped Octopus action '{sourceAction.Name}' produces enabled Squid action type '{mappedAction.ActionType}', but no registered Squid runtime action handler can handle it.",
                ResourceType = OctopusResourceKind.DeploymentAction.ToString(),
                SourceId = sourceAction.Id,
                ResourceName = sourceAction.Name
            })
        ];
    }

    private static DeploymentActionDto ToRuntimeAction(CreateOrUpdateDeploymentActionModel action)
        => new()
        {
            Name = action.Name,
            ActionType = action.ActionType,
            WorkerPoolId = action.WorkerPoolId,
            IsDisabled = action.IsDisabled,
            IsRequired = action.IsRequired,
            CanBeUsedForProjectVersioning = action.CanBeUsedForProjectVersioning,
            Properties = (action.Properties ?? [])
                .Select(p => new DeploymentActionPropertyDto
                {
                    PropertyName = p.PropertyName,
                    PropertyValue = p.PropertyValue
                })
                .ToList(),
            Environments = action.Environments?.ToList() ?? [],
            ExcludedEnvironments = action.ExcludedEnvironments?.ToList() ?? [],
            Channels = action.Channels?.ToList() ?? []
        };
}
