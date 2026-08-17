using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Mapping;

public interface IOctopusImportDeploymentProcessMapper : IScopedDependency
{
    OctopusImportDeploymentProcessMappingResult MapToCreateStepCommands(
        OctopusResourceNode deploymentProcessResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId);
}

public class OctopusImportDeploymentProcessMapper : IOctopusImportDeploymentProcessMapper
{
    private const string StepTypeAction = "Action";
    private const string OctopusTargetRolesPropertyName = "Octopus.Action.TargetRoles";
    private const string OctopusRunOnServerPropertyName = "Octopus.Action.RunOnServer";
    private const string OctopusConditionExpressionPropertyName = "Octopus.Action.ConditionExpression";
    private const string OctopusStepConditionExpressionPropertyName = "Octopus.Step.ConditionExpression";
    private const string OctopusMaxParallelismPropertyName = "Octopus.Action.MaxParallelism";
    private const string OctopusTimeoutPropertyName = "Octopus.Action.Timeout";

    public OctopusImportDeploymentProcessMappingResult MapToCreateStepCommands(
        OctopusResourceNode deploymentProcessResource,
        OctopusImportIdMap idMap,
        int destinationSpaceId)
    {
        ArgumentNullException.ThrowIfNull(deploymentProcessResource);
        ArgumentNullException.ThrowIfNull(idMap);

        if (deploymentProcessResource.Kind != OctopusResourceKind.DeploymentProcess)
            throw new ArgumentException("Octopus deployment process mapper requires a current deployment process resource.", nameof(deploymentProcessResource));

        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        var process = deploymentProcessResource.GetSource<OctopusDeploymentProcessDto>()
            ?? throw new ArgumentException("Octopus deployment process resource does not contain an OctopusDeploymentProcessDto source.", nameof(deploymentProcessResource));

        var diagnostics = new List<OctopusImportDiagnosticDto>();
        var destinationProcessId = MapDestinationProcessId(process, idMap, deploymentProcessResource, diagnostics);
        var steps = (process.Steps ?? [])
            .Select((step, index) => MapStep(step, index, destinationProcessId, destinationSpaceId, idMap, diagnostics))
            .ToList();

        return new OctopusImportDeploymentProcessMappingResult(steps, diagnostics);
    }

    private static int MapDestinationProcessId(
        OctopusDeploymentProcessDto process,
        OctopusImportIdMap idMap,
        OctopusResourceNode processResource,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (idMap.TryGetDestinationId(process.Id, OctopusResourceKind.DeploymentProcess.ToString(), out var destinationProcessId))
            return destinationProcessId;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportDeploymentProcessMappingDiagnosticCodes.MissingProcessMapping,
            $"Octopus deployment process '{process.Id}' has not been mapped to a destination deployment process.",
            processResource));

        return default;
    }

    private static OctopusImportDeploymentStepCommandMapping MapStep(
        OctopusDeploymentStepDto step,
        int stepIndex,
        int destinationProcessId,
        int destinationSpaceId,
        OctopusImportIdMap idMap,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var actions = (step.Actions ?? [])
            .Select((action, actionIndex) => MapAction(action, actionIndex, idMap, diagnostics))
            .ToList();

        var stepProperties = MapStepProperties(step, diagnostics);

        foreach (var targetRole in actions.SelectMany(a => GetTargetRoles(a.SourceAction)))
            AddOrAppendStepProperty(stepProperties, SpecialVariables.Step.TargetRoles, targetRole);

        foreach (var action in actions.Where(a => IsRunOnServer(a.SourceAction)))
            AddOrAppendStepProperty(stepProperties, SpecialVariables.Step.RunOnServer, "true");

        var stepModel = new CreateOrUpdateDeploymentStepModel
        {
            Name = step.Name,
            StepType = StepTypeAction,
            Condition = step.Condition,
            StartTrigger = step.StartTrigger,
            PackageRequirement = MapPackageRequirement(step.PackageRequirement, step, diagnostics),
            IsDisabled = actions.Count > 0 && actions.All(a => a.Action.IsDisabled),
            IsRequired = actions.Count == 0 || actions.All(a => a.Action.IsRequired),
            Properties = stepProperties,
            Actions = actions.Select(a => a.Action).ToList()
        };

        var command = new CreateDeploymentStepCommand
        {
            ProcessId = destinationProcessId,
            SpaceId = destinationSpaceId,
            Step = stepModel
        };

        return new OctopusImportDeploymentStepCommandMapping(
            step.Id,
            step.Name,
            stepIndex,
            command,
            actions.Select(a => new OctopusImportDeploymentActionModelMapping(a.SourceAction.Id, a.SourceAction.Name, a.SourceIndex, a.Action)).ToList());
    }

    private static ActionMapping MapAction(
        OctopusDeploymentActionDto action,
        int actionIndex,
        OctopusImportIdMap idMap,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var actionType = MapActionType(action, diagnostics);
        var isUnsupportedActionType = string.Equals(actionType, action.ActionType, StringComparison.OrdinalIgnoreCase)
            && !SpecialVariables.ActionTypes.All.Contains(actionType);

        var model = new CreateOrUpdateDeploymentActionModel
        {
            Name = action.Name,
            ActionType = actionType,
            IsDisabled = action.IsDisabled || isUnsupportedActionType,
            IsRequired = action.IsRequired,
            WorkerPoolId = MapWorkerPool(action, diagnostics),
            CanBeUsedForProjectVersioning = false,
            Properties = MapActionProperties(action, diagnostics),
            Environments = MapScopedIds(action, action.Environments, OctopusResourceKind.Environment, OctopusImportDeploymentProcessMappingDiagnosticCodes.MissingEnvironmentMapping, "environment", idMap, diagnostics),
            ExcludedEnvironments = MapScopedIds(action, action.ExcludedEnvironments, OctopusResourceKind.Environment, OctopusImportDeploymentProcessMappingDiagnosticCodes.MissingExcludedEnvironmentMapping, "excluded environment", idMap, diagnostics),
            Channels = MapScopedIds(action, action.Channels, OctopusResourceKind.Channel, OctopusImportDeploymentProcessMappingDiagnosticCodes.MissingChannelMapping, "channel", idMap, diagnostics)
        };

        AddUnsupportedVariableScopeDiagnostics(action, diagnostics);
        AddUnsupportedTenantDiagnostics(action, diagnostics);
        AddUnsupportedActionConditionDiagnostic(action, diagnostics);

        return new ActionMapping(action, actionIndex, model);
    }

    private static string MapActionType(
        OctopusDeploymentActionDto action,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var actionType = action.ActionType?.Trim();

        if (string.Equals(actionType, "Octopus.KubernetesDeployContainers", StringComparison.OrdinalIgnoreCase))
            return SpecialVariables.ActionTypes.KubernetesDeployContainers;

        if (string.Equals(actionType, "Octopus.KubernetesDeployIngress", StringComparison.OrdinalIgnoreCase))
            return SpecialVariables.ActionTypes.KubernetesDeployIngress;

        if (string.Equals(actionType, "Octopus.Script", StringComparison.OrdinalIgnoreCase))
            return SpecialVariables.ActionTypes.Script;

        if (string.Equals(actionType, "Octopus.Manual", StringComparison.OrdinalIgnoreCase))
            return SpecialVariables.ActionTypes.Manual;

        if (string.Equals(actionType, "Octopus.IIS", StringComparison.OrdinalIgnoreCase))
            return SpecialVariables.ActionTypes.DeployToIISWebSite;

        if (!string.IsNullOrWhiteSpace(actionType) && actionType.Contains("WindowsService", StringComparison.OrdinalIgnoreCase))
            return SpecialVariables.ActionTypes.DeployWindowsService;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportDeploymentProcessMappingDiagnosticCodes.UnsupportedActionType,
            $"Octopus action type '{action.ActionType}' for action '{action.Name}' is not supported by the current Squid import process mapper.",
            OctopusResourceKind.DeploymentAction,
            action.Id,
            action.Name));

        return actionType;
    }

    private static int? MapWorkerPool(
        OctopusDeploymentActionDto action,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(action.WorkerPoolId) && string.IsNullOrWhiteSpace(action.WorkerPoolVariable))
            return null;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportDeploymentProcessMappingDiagnosticCodes.WorkerPoolUnsupported,
            $"Octopus worker pool configuration for action '{action.Name}' cannot be mapped until worker pools are imported or selected.",
            OctopusResourceKind.DeploymentAction,
            action.Id,
            action.Name));

        return null;
    }

    private static List<ActionPropertyModel> MapActionProperties(
        OctopusDeploymentActionDto action,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var properties = new List<ActionPropertyModel>();
        var unmappedPropertyNames = (action.Properties ?? [])
            .Keys
            .Where(propertyName => !IsStepLevelActionProperty(propertyName))
            .ToList();

        if (unmappedPropertyNames.Count > 0 || action.Container != null || action.Packages?.Count > 0 || action.GitDependencies?.Count > 0)
        {
            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportDeploymentProcessMappingDiagnosticCodes.ActionPropertyMappingDeferred,
                $"Octopus action properties for action '{action.Name}' require the action-specific import mapper before they can be translated into Squid action properties.",
                OctopusResourceKind.DeploymentAction,
                action.Id,
                action.Name));
        }

        return properties;
    }

    private static bool IsStepLevelActionProperty(string propertyName)
        => string.Equals(propertyName, OctopusTargetRolesPropertyName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(propertyName, OctopusRunOnServerPropertyName, StringComparison.OrdinalIgnoreCase);

    private static List<StepPropertyModel> MapStepProperties(
        OctopusDeploymentStepDto step,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var properties = new List<StepPropertyModel>();
        var sourceProperties = step.Properties ?? [];

        AddMappedStepProperty(properties, sourceProperties, OctopusTargetRolesPropertyName, SpecialVariables.Step.TargetRoles);
        AddMappedStepProperty(properties, sourceProperties, OctopusRunOnServerPropertyName, SpecialVariables.Step.RunOnServer);
        AddMappedStepProperty(properties, sourceProperties, OctopusConditionExpressionPropertyName, SpecialVariables.Step.ConditionExpression);
        AddMappedStepProperty(properties, sourceProperties, OctopusStepConditionExpressionPropertyName, SpecialVariables.Step.ConditionExpression);
        AddMappedStepProperty(properties, sourceProperties, OctopusMaxParallelismPropertyName, SpecialVariables.Step.MaxParallelism);
        AddMappedStepProperty(properties, sourceProperties, OctopusTimeoutPropertyName, SpecialVariables.Step.Timeout);

        return properties;
    }

    private static void AddMappedStepProperty(
        List<StepPropertyModel> properties,
        Dictionary<string, string> sourceProperties,
        string sourceName,
        string destinationName)
    {
        if (!sourceProperties.TryGetValue(sourceName, out var value) || string.IsNullOrWhiteSpace(value))
            return;

        AddOrAppendStepProperty(properties, destinationName, value);
    }

    private static void AddOrAppendStepProperty(List<StepPropertyModel> properties, string propertyName, string propertyValue)
    {
        if (string.IsNullOrWhiteSpace(propertyValue))
            return;

        var existing = properties.FirstOrDefault(p => string.Equals(p.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            properties.Add(new StepPropertyModel
            {
                PropertyName = propertyName,
                PropertyValue = propertyValue
            });
            return;
        }

        var values = SplitReferenceList(existing.PropertyValue)
            .Concat(SplitReferenceList(propertyValue))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        existing.PropertyValue = string.Join(",", values);
    }

    private static string MapPackageRequirement(
        string packageRequirement,
        OctopusDeploymentStepDto step,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(packageRequirement))
            return packageRequirement;

        if (string.Equals(packageRequirement, "LetOctopusDecide", StringComparison.OrdinalIgnoreCase))
            return SpecialVariables.PackageRequirements.LetSquidDecide;

        if (string.Equals(packageRequirement, SpecialVariables.PackageRequirements.BeforePackageAcquisition, StringComparison.OrdinalIgnoreCase))
            return SpecialVariables.PackageRequirements.BeforePackageAcquisition;

        if (string.Equals(packageRequirement, SpecialVariables.PackageRequirements.AfterPackageAcquisition, StringComparison.OrdinalIgnoreCase))
            return SpecialVariables.PackageRequirements.AfterPackageAcquisition;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Warning,
            OctopusImportDeploymentProcessMappingDiagnosticCodes.UnsupportedPackageRequirement,
            $"Octopus package requirement '{packageRequirement}' for step '{step.Name}' is not recognized by the current Squid import process mapper and was preserved as-is.",
            OctopusResourceKind.DeploymentStep,
            step.Id,
            step.Name));

        return packageRequirement;
    }

    private static List<int> MapScopedIds(
        OctopusDeploymentActionDto action,
        List<string> sourceIds,
        OctopusResourceKind sourceKind,
        string diagnosticCode,
        string scopeName,
        OctopusImportIdMap idMap,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        var destinationIds = new List<int>();

        foreach (var sourceId in sourceIds ?? [])
        {
            if (idMap.TryGetDestinationId(sourceId, sourceKind.ToString(), out var destinationId))
            {
                destinationIds.Add(destinationId);
                continue;
            }

            diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                diagnosticCode,
                $"Octopus action '{action.Name}' references {scopeName} '{sourceId}', which has not been mapped to a destination Squid resource.",
                OctopusResourceKind.DeploymentAction,
                action.Id,
                action.Name));
        }

        return destinationIds;
    }

    private static void AddUnsupportedVariableScopeDiagnostics(
        OctopusDeploymentActionDto action,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(action.EnvironmentsVariable)
            && string.IsNullOrWhiteSpace(action.ExcludedEnvironmentsVariable)
            && string.IsNullOrWhiteSpace(action.ChannelsVariable))
        {
            return;
        }

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportDeploymentProcessMappingDiagnosticCodes.VariableScopedActionTargetUnsupported,
            $"Octopus variable-scoped environment or channel targeting for action '{action.Name}' cannot be represented in Squid step commands.",
            OctopusResourceKind.DeploymentAction,
            action.Id,
            action.Name));
    }

    private static void AddUnsupportedTenantDiagnostics(
        OctopusDeploymentActionDto action,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if ((action.TenantTags?.Count ?? 0) == 0 && string.IsNullOrWhiteSpace(action.TenantTagsVariable))
            return;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportDeploymentProcessMappingDiagnosticCodes.TenantTagsUnsupported,
            $"Octopus tenant-tag targeting for action '{action.Name}' cannot be represented in Squid step commands.",
            OctopusResourceKind.DeploymentAction,
            action.Id,
            action.Name));
    }

    private static void AddUnsupportedActionConditionDiagnostic(
        OctopusDeploymentActionDto action,
        List<OctopusImportDiagnosticDto> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(action.Condition))
            return;

        diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportDeploymentProcessMappingDiagnosticCodes.UnsupportedActionCondition,
            $"Octopus per-action condition '{action.Condition}' for action '{action.Name}' cannot be represented in Squid step commands.",
            OctopusResourceKind.DeploymentAction,
            action.Id,
            action.Name));
    }

    private static IEnumerable<string> GetTargetRoles(OctopusDeploymentActionDto action)
    {
        if (action.Properties == null || !action.Properties.TryGetValue(OctopusTargetRolesPropertyName, out var targetRoles))
            return [];

        return SplitReferenceList(targetRoles);
    }

    private static bool IsRunOnServer(OctopusDeploymentActionDto action)
        => action.Properties != null
           && action.Properties.TryGetValue(OctopusRunOnServerPropertyName, out var runOnServer)
           && bool.TryParse(runOnServer, out var parsed)
           && parsed;

    private static IEnumerable<string> SplitReferenceList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v));
    }

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        OctopusResourceNode resource)
        => Diagnostic(severity, code, message, resource.Kind, resource.SourceId, resource.Name);

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        OctopusResourceKind resourceKind,
        string sourceId,
        string resourceName)
        => new()
        {
            Severity = severity,
            Code = code,
            Message = message,
            ResourceType = resourceKind.ToString(),
            SourceId = sourceId,
            ResourceName = resourceName
        };

    private sealed record ActionMapping(
        OctopusDeploymentActionDto SourceAction,
        int SourceIndex,
        CreateOrUpdateDeploymentActionModel Action);
}

public sealed record OctopusImportDeploymentProcessMappingResult(
    IReadOnlyList<OctopusImportDeploymentStepCommandMapping> Steps,
    IReadOnlyList<OctopusImportDiagnosticDto> Diagnostics)
{
    public bool HasBlockers => Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker);
}

public sealed record OctopusImportDeploymentStepCommandMapping(
    string SourceStepId,
    string SourceStepName,
    int SourceIndex,
    CreateDeploymentStepCommand CreateCommand,
    IReadOnlyList<OctopusImportDeploymentActionModelMapping> Actions);

public sealed record OctopusImportDeploymentActionModelMapping(
    string SourceActionId,
    string SourceActionName,
    int ActionIndex,
    CreateOrUpdateDeploymentActionModel Action);
