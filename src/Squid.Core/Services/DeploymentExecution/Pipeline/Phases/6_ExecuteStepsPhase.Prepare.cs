using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.VariableSubstitution;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed partial class ExecuteStepsPhase
{
    private record PreparedAction(ActionExecutionResult Result, List<VariableDto> EffectiveVariables);

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

            var prepared = await handler.PrepareAsync(context, ct).ConfigureAwait(false);

            if (prepared != null)
            {
                prepared.ActionName = action.Name;
                prepared.ActionProperties = BuildActionPropertyDictionary(expandedAction);

                var executionMode = prepared.ResolveExecutionMode();
                var contextPreparationPolicy = ResolveContextPreparationPolicy(prepared, tc);

                if (prepared.ScriptBody != null)
                    prepared.ScriptBody = VariableExpander.ExpandString(prepared.ScriptBody, variableDictionary);

                // Direct script can be wrapped here. Packaged payloads are wrapped later after payload template paths are resolved.
                if (executionMode == ExecutionMode.DirectScript
                    && contextPreparationPolicy == ContextPreparationPolicy.Apply)
                    WrapScriptIfApplicable(prepared, tc, actionEffective);

                stepResults.Add(new PreparedAction(prepared, actionEffective));
            }
        }

        return stepResults;
    }

    private static void WrapScriptIfApplicable(ActionExecutionResult prepared, DeploymentTargetContext tc, List<VariableDto> effectiveVariables)
    {
        var wrapper = tc.Transport?.ScriptWrapper;

        if (wrapper == null) return;

        var scriptContext = new ScriptContext
        {
            Endpoint = tc.EndpointContext,
            Syntax = prepared.Syntax,
            Variables = effectiveVariables,
            ActionProperties = prepared.ActionProperties
        };

        prepared.ScriptBody = wrapper.WrapScript(prepared.ScriptBody, scriptContext);
    }

    private ScriptExecutionRequest BuildScriptExecutionRequest(ActionExecutionResult actionResult, DeploymentTargetContext tc, List<VariableDto> effectiveVariables, DeploymentStepDto step, TimeSpan? stepTimeout = null)
    {
        var resolvedMode = actionResult.ResolveExecutionMode();
        var resolvedContextPreparationPolicy = ResolveContextPreparationPolicy(actionResult, tc);
        var masker = BuildSensitiveMasker(effectiveVariables);

        return new ScriptExecutionRequest
        {
            ScriptBody = actionResult.ScriptBody,
            Files = actionResult.Files,
            CalamariCommand = actionResult.CalamariCommand,
            ExecutionMode = resolvedMode,
            ContextPreparationPolicy = resolvedContextPreparationPolicy,
            ExecutionLocation = tc.Transport?.ExecutionLocation ?? ExecutionLocation.Unspecified,
            ExecutionBackend = tc.Transport?.ExecutionBackend ?? ExecutionBackend.Unspecified,
            PayloadKind = actionResult.PayloadKind,
            RunnerKind = actionResult.RunnerKind,
            Syntax = actionResult.Syntax,
            ActionProperties = actionResult.ActionProperties,
            EndpointContext = tc.EndpointContext,
            Variables = effectiveVariables,
            Machine = tc.Machine,
            ReleaseVersion = _ctx.Release?.Version,
            ContextWrapper = resolvedContextPreparationPolicy == ContextPreparationPolicy.Apply ? tc.Transport?.ScriptWrapper : null,
            Timeout = stepTimeout,
            Masker = masker,
            TargetNamespace = ResolveTargetNamespace(effectiveVariables),
            ServerTaskId = _ctx.ServerTaskId,
            StepName = step.Name,
            ActionName = actionResult.ActionName
        };
    }

    private static string? ResolveTargetNamespace(List<VariableDto> variables)
    {
        var namespaceVar = variables.FirstOrDefault(v => v.Name == SpecialVariables.Kubernetes.Namespace);

        return namespaceVar?.Value;
    }

    private static SensitiveValueMasker BuildSensitiveMasker(List<VariableDto> variables)
    {
        var sensitiveValues = variables.Where(v => v.IsSensitive && !string.IsNullOrEmpty(v.Value)).Select(v => v.Value);
        var masker = new SensitiveValueMasker(sensitiveValues);

        return masker.ValueCount > 0 ? masker : null;
    }

    private static Dictionary<string, string> BuildActionPropertyDictionary(DeploymentActionDto action)
    {
        if (action.Properties == null || action.Properties.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var dict = new Dictionary<string, string>(action.Properties.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var prop in action.Properties)
            dict[prop.PropertyName] = prop.PropertyValue;

        return dict;
    }

    private static HashSet<string> ExtractStepRoles(DeploymentStepDto step)
    {
        var rolesProp = step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.TargetRoles);

        if (rolesProp == null || string.IsNullOrEmpty(rolesProp.PropertyValue))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return DeploymentTargetFinder.ParseCsvRoles(rolesProp.PropertyValue);
    }

    private static ContextPreparationPolicy ResolveContextPreparationPolicy(ActionExecutionResult actionResult, DeploymentTargetContext tc)
    {
        if (actionResult.ContextPreparationPolicy != ContextPreparationPolicy.Unspecified)
            return actionResult.ContextPreparationPolicy;

        var mode = actionResult.ResolveExecutionMode();

        if (mode == ExecutionMode.PackagedPayload && (tc.Transport?.RequiresContextPreparationForPackagedPayload ?? false))
            return ContextPreparationPolicy.Apply;

        return actionResult.ResolveContextPreparationPolicy();
    }
}
