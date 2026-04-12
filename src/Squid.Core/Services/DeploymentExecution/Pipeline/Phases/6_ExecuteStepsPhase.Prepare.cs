using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.VariableSubstitution;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed partial class ExecuteStepsPhase
{
    private record PreparedAction(
        List<VariableDto> EffectiveVariables,
        IActionHandler Handler,
        ActionExecutionContext Context,
        VariableDictionary VariableDictionary);

    private async Task<List<PreparedAction>> PrepareStepActionsAsync(
        DeploymentStepDto step,
        List<DeploymentActionDto> eligibleActions,
        VariableScopeContext baseScopeContext,
        DeploymentTargetContext tc,
        int stepDisplayOrder,
        CancellationToken ct)
    {
        var stepResults = new List<PreparedAction>();

        foreach (var action in eligibleActions)
        {
            if (actionHandlerRegistry.ResolveScope(action) == ExecutionScope.StepLevel)
                continue;

            var handler = actionHandlerRegistry.Resolve(action);

            if (handler == null)
            {
                Log.Warning("[Deploy] No handler found for action {ActionType}, skipping", action.ActionType);

                await lifecycle.EmitAsync(new ActionNoHandlerEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, ActionType = action.ActionType }), ct).ConfigureAwait(false);

                continue;
            }

            await lifecycle.EmitAsync(new ActionRunningEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, ActionName = action.Name, MachineName = tc.Machine.Name }), ct).ConfigureAwait(false);

            var actionScopeContext = baseScopeContext with { ActionId = action.Id, ActionName = action.Name };
            var actionEffective = EffectiveVariableBuilder.BuildEffectiveVariables(_ctx.Variables, tc, actionScopeContext);
            var actionVariables = EffectiveVariableBuilder.BuildActionVariables(actionEffective, action, _ctx.SelectedPackages);
            var variableDictionary = VariableDictionaryFactory.Create(actionVariables);
            var expandedAction = VariableExpander.ExpandActionProperties(action, variableDictionary);

            var context = new ActionExecutionContext
            {
                Step = step,
                Action = expandedAction,
                Variables = actionVariables,
                ReleaseVersion = _ctx.Release?.Version,
                SelectedPackages = _ctx.SelectedPackages?
                    .Select(sp => new Message.Models.Deployments.Release.SelectedPackageDto
                    {
                        ActionName = sp.ActionName,
                        PackageReferenceName = sp.PackageReferenceName,
                        Version = sp.Version
                    }).ToList() ?? new()
            };

            stepResults.Add(new PreparedAction(actionEffective, handler, context, variableDictionary));
        }

        return stepResults;
    }

    private List<PackageAcquisitionResult> BuildPackageReferences(string? actionName)
    {
        if (string.IsNullOrEmpty(actionName) || _ctx.SelectedPackages.Count == 0)
            return new List<PackageAcquisitionResult>();

        var names = _ctx.SelectedPackages
            .Where(p => string.Equals(p.ActionName, actionName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.PackageReferenceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _ctx.AcquiredPackages
            .Where(kv => names.Contains(kv.Key))
            .Select(kv => kv.Value)
            .ToList();
    }

    private static SensitiveValueMasker BuildSensitiveMasker(List<VariableDto> variables)
    {
        var sensitiveValues = variables.Where(v => v.IsSensitive && !string.IsNullOrEmpty(v.Value)).Select(v => v.Value);
        var masker = new SensitiveValueMasker(sensitiveValues);

        return masker.ValueCount > 0 ? masker : null;
    }

    private static HashSet<string> ExtractStepRoles(DeploymentStepDto step)
    {
        var rolesProp = step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.TargetRoles);

        if (rolesProp == null || string.IsNullOrEmpty(rolesProp.PropertyValue))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return DeploymentTargetFinder.ParseCsvRoles(rolesProp.PropertyValue);
    }

    private async Task EmitPreparationWarningsAsync(List<string> warnings, int stepDisplayOrder, string actionName, string machineName, CancellationToken ct)
    {
        if (warnings.Count == 0) return;

        foreach (var warning in warnings)
        {
            await lifecycle.EmitAsync(new ActionPreparationWarningEvent(new DeploymentEventContext
            {
                StepDisplayOrder = stepDisplayOrder,
                ActionName = actionName,
                MachineName = machineName,
                Message = warning
            }), ct).ConfigureAwait(false);
        }
    }
}
