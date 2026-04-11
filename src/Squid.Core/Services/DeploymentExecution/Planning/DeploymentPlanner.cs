using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Planning.Exceptions;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Planning;

/// <summary>
/// Default <see cref="IDeploymentPlanner"/> implementation. The planner is a pure function
/// over <see cref="DeploymentPlanRequest"/> — it walks steps in order, filters runnable
/// actions, resolves matched targets per step, and validates capabilities on every
/// (action × target) pair using <see cref="ICapabilityValidator"/>.
///
/// <para>
/// Phase 6a: the planner produces a lightweight <see cref="RunScriptIntent"/> stub as the
/// intent for each dispatch. This is enough to exercise the transport-level checks
/// (action-type whitelist, default script syntax, nested-file support, required-feature
/// matching, package staging) so the structural shape of Preview and Execute is the same.
/// Phase 9 replaces the stub with <c>IActionHandler.DescribeIntentAsync</c> so the intent
/// is the handler's real output.
/// </para>
/// </summary>
public sealed class DeploymentPlanner : IDeploymentPlanner
{
    private readonly IActionHandlerRegistry _actionHandlerRegistry;
    private readonly ICapabilityValidator _capabilityValidator;

    public DeploymentPlanner(IActionHandlerRegistry actionHandlerRegistry, ICapabilityValidator capabilityValidator)
    {
        ArgumentNullException.ThrowIfNull(actionHandlerRegistry);
        ArgumentNullException.ThrowIfNull(capabilityValidator);

        _actionHandlerRegistry = actionHandlerRegistry;
        _capabilityValidator = capabilityValidator;
    }

    public Task<DeploymentPlan> PlanAsync(DeploymentPlanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var candidateTargets = BuildCandidateTargets(request.TargetContexts);
        var blockers = new List<PlanBlockingReason>();
        var plannedSteps = new List<PlannedStep>();

        foreach (var step in request.Steps.OrderBy(s => s.StepOrder))
        {
            var planned = PlanStep(step, request, candidateTargets, blockers);

            plannedSteps.Add(planned);
        }

        AddGlobalBlockers(request, plannedSteps, candidateTargets, blockers);

        var plan = new DeploymentPlan
        {
            Mode = request.Mode,
            ReleaseId = request.ReleaseId,
            EnvironmentId = request.EnvironmentId,
            DeploymentProcessSnapshotId = request.DeploymentProcessSnapshotId,
            Steps = plannedSteps,
            CandidateTargets = candidateTargets,
            BlockingReasons = DedupeBlockers(blockers)
        };

        if (request.Mode == PlanMode.Execute && plan.BlockingReasons.Count > 0)
            throw new DeploymentPlanValidationException(plan);

        return Task.FromResult(plan);
    }

    // ---------- candidate target construction ---------------------------

    private static List<PlannedTarget> BuildCandidateTargets(IReadOnlyList<DeploymentTargetContext> contexts)
    {
        return contexts
            .Where(tc => tc?.Machine != null && !tc.IsExcluded)
            .OrderBy(tc => tc.Machine.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToPlannedTarget)
            .ToList();
    }

    private static PlannedTarget ToPlannedTarget(DeploymentTargetContext tc) => new()
    {
        MachineId = tc.Machine.Id,
        MachineName = tc.Machine.Name,
        Roles = DeploymentTargetFinder.ParseRoles(tc.Machine.Roles)
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        CommunicationStyle = tc.CommunicationStyle
    };

    // ---------- per-step planning ---------------------------------------

    private PlannedStep PlanStep(
        DeploymentStepDto step,
        DeploymentPlanRequest request,
        IReadOnlyList<PlannedTarget> candidateTargets,
        List<PlanBlockingReason> blockers)
    {
        if (step.IsDisabled)
            return SkippedStep(step, PlannedStepStatus.Disabled, $"Step \"{step.Name}\" is disabled.");

        var runnableActions = FilterRunnableActions(step, request);

        if (runnableActions.Count == 0)
            return SkippedStep(step, PlannedStepStatus.NoRunnableActions,
                $"Step \"{step.Name}\" has no runnable actions for the selected environment/channel.");

        if (AllActionsStepLevel(runnableActions))
            return BuildStepLevelStep(step, runnableActions);

        if (RunOnServerEvaluator.IsRunOnServer(step))
            return BuildRunOnServerStep(step, runnableActions);

        var requiredRoles = ExtractRequiredRoles(step);
        var matchedTargets = FilterTargetsByRoles(candidateTargets, requiredRoles);

        if (matchedTargets.Count == 0)
        {
            blockers.Add(BuildNoMatchingTargetsBlocker(step, requiredRoles));

            return SkippedStep(step, PlannedStepStatus.NoMatchingTargets,
                BuildNoMatchingTargetsMessage(step, requiredRoles)) with
            {
                RequiredRoles = requiredRoles
            };
        }

        var actions = BuildTargetLevelActions(runnableActions, matchedTargets, request, step, blockers);

        return new PlannedStep
        {
            StepId = step.Id,
            StepName = step.Name,
            StepOrder = step.StepOrder,
            Status = PlannedStepStatus.Applicable,
            StatusMessage = string.Empty,
            RequiredRoles = requiredRoles,
            MatchedTargets = matchedTargets,
            Actions = actions
        };
    }

    // ---------- runnable action filtering -------------------------------

    private List<DeploymentActionDto> FilterRunnableActions(DeploymentStepDto step, DeploymentPlanRequest request)
    {
        if (step.Actions is null || step.Actions.Count == 0)
            return new List<DeploymentActionDto>();

        var ctx = new ActionEvaluationContext(
            request.EnvironmentId,
            request.ChannelId,
            request.SkipActionIds is HashSet<int> set ? set : new HashSet<int>(request.SkipActionIds));

        return step.Actions
            .Where(a => StepEligibilityEvaluator.EvaluateAction(a, ctx).ShouldExecute)
            .OrderBy(a => a.ActionOrder)
            .ToList();
    }

    private bool AllActionsStepLevel(List<DeploymentActionDto> runnableActions)
    {
        return runnableActions.All(a => _actionHandlerRegistry.ResolveScope(a) == ExecutionScope.StepLevel);
    }

    // ---------- step-level / run-on-server shortcuts --------------------

    private PlannedStep BuildStepLevelStep(DeploymentStepDto step, List<DeploymentActionDto> runnableActions)
    {
        var actions = runnableActions
            .Select(a => new PlannedAction
            {
                ActionId = a.Id,
                ActionName = a.Name,
                ActionType = a.ActionType,
                ActionOrder = a.ActionOrder,
                IsStepLevel = true,
                Dispatches = Array.Empty<PlannedTargetDispatch>()
            })
            .ToList();

        return new PlannedStep
        {
            StepId = step.Id,
            StepName = step.Name,
            StepOrder = step.StepOrder,
            Status = PlannedStepStatus.StepLevelOnly,
            StatusMessage = $"Step \"{step.Name}\" runs at step-level (no per-target dispatches).",
            Actions = actions
        };
    }

    private PlannedStep BuildRunOnServerStep(DeploymentStepDto step, List<DeploymentActionDto> runnableActions)
    {
        var actions = runnableActions
            .Select(a => new PlannedAction
            {
                ActionId = a.Id,
                ActionName = a.Name,
                ActionType = a.ActionType,
                ActionOrder = a.ActionOrder,
                IsStepLevel = _actionHandlerRegistry.ResolveScope(a) == ExecutionScope.StepLevel,
                Dispatches = Array.Empty<PlannedTargetDispatch>()
            })
            .ToList();

        return new PlannedStep
        {
            StepId = step.Id,
            StepName = step.Name,
            StepOrder = step.StepOrder,
            Status = PlannedStepStatus.RunOnServer,
            StatusMessage = $"Step \"{step.Name}\" is marked RunOnServer.",
            Actions = actions
        };
    }

    // ---------- target-level action construction ------------------------

    private List<PlannedAction> BuildTargetLevelActions(
        List<DeploymentActionDto> runnableActions,
        List<PlannedTarget> matchedTargets,
        DeploymentPlanRequest request,
        DeploymentStepDto step,
        List<PlanBlockingReason> blockers)
    {
        var contextsByMachineId = request.TargetContexts.ToDictionary(tc => tc.Machine.Id);
        var result = new List<PlannedAction>(runnableActions.Count);

        foreach (var action in runnableActions)
        {
            var dispatches = BuildDispatchesForAction(action, matchedTargets, contextsByMachineId, request, step, blockers);

            result.Add(new PlannedAction
            {
                ActionId = action.Id,
                ActionName = action.Name,
                ActionType = action.ActionType,
                ActionOrder = action.ActionOrder,
                IsStepLevel = false,
                Dispatches = dispatches
            });
        }

        return result;
    }

    private List<PlannedTargetDispatch> BuildDispatchesForAction(
        DeploymentActionDto action,
        List<PlannedTarget> matchedTargets,
        IReadOnlyDictionary<int, DeploymentTargetContext> contextsByMachineId,
        DeploymentPlanRequest request,
        DeploymentStepDto step,
        List<PlanBlockingReason> blockers)
    {
        var dispatches = new List<PlannedTargetDispatch>(matchedTargets.Count);
        var intent = BuildStubIntent(step, action);

        foreach (var target in matchedTargets)
        {
            var dispatch = BuildDispatch(intent, action, target, contextsByMachineId, request, blockers);

            dispatches.Add(dispatch);
        }

        return dispatches;
    }

    private PlannedTargetDispatch BuildDispatch(
        ExecutionIntent intent,
        DeploymentActionDto action,
        PlannedTarget target,
        IReadOnlyDictionary<int, DeploymentTargetContext> contextsByMachineId,
        DeploymentPlanRequest request,
        List<PlanBlockingReason> blockers)
    {
        if (!contextsByMachineId.TryGetValue(target.MachineId, out var targetContext) || targetContext.Transport is null)
        {
            blockers.Add(BuildTransportUnresolvedBlocker(target));

            return new PlannedTargetDispatch
            {
                Target = target,
                Intent = intent,
                Validation = CapabilityValidationResult.Supported
            };
        }

        var capabilities = targetContext.Transport.Capabilities;
        var validation = ValidateCapabilities(intent, capabilities, target.CommunicationStyle, action.ActionType);

        if (!validation.IsValid)
            AddCapabilityBlockers(blockers, validation.Violations, target);

        return new PlannedTargetDispatch
        {
            Target = target,
            Intent = intent,
            Validation = validation
        };
    }

    // ---------- capability validation wiring ----------------------------

    private CapabilityValidationResult ValidateCapabilities(
        ExecutionIntent intent,
        ITransportCapabilities capabilities,
        CommunicationStyle communicationStyle,
        string actionType)
    {
        var violations = _capabilityValidator.Validate(intent, capabilities, communicationStyle, actionType);

        if (violations.Count == 0)
            return CapabilityValidationResult.Supported;

        return new CapabilityValidationResult { Violations = violations };
    }

    private static void AddCapabilityBlockers(
        List<PlanBlockingReason> blockers,
        IReadOnlyList<CapabilityViolation> violations,
        PlannedTarget target)
    {
        foreach (var violation in violations)
        {
            blockers.Add(new PlanBlockingReason
            {
                Code = PlanBlockingReasonCodes.CapabilityViolation,
                Message = violation.Message,
                StepName = violation.StepName,
                MachineId = target.MachineId,
                MachineName = target.MachineName,
                Detail = violation.Code
            });
        }
    }

    // ---------- intent stub (Phase 6a) ----------------------------------

    private static ExecutionIntent BuildStubIntent(DeploymentStepDto step, DeploymentActionDto action)
    {
        return new RunScriptIntent
        {
            Name = $"plan:{action.ActionType}",
            StepName = step.Name,
            ActionName = action.Name,
            ScriptBody = string.Empty,
            InjectRuntimeBundle = false
        };
    }

    // ---------- required-roles helpers ----------------------------------

    private static List<string> ExtractRequiredRoles(DeploymentStepDto step)
    {
        var rolesProperty = step.Properties?
            .FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.TargetRoles);

        if (rolesProperty == null || string.IsNullOrWhiteSpace(rolesProperty.PropertyValue))
            return new List<string>();

        return DeploymentTargetFinder.ParseCsvRoles(rolesProperty.PropertyValue)
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<PlannedTarget> FilterTargetsByRoles(IReadOnlyList<PlannedTarget> candidates, List<string> requiredRoles)
    {
        if (requiredRoles.Count == 0)
            return candidates.ToList();

        var roleSet = new HashSet<string>(requiredRoles, StringComparer.OrdinalIgnoreCase);

        return candidates
            .Where(t => t.Roles.Any(r => roleSet.Contains(r)))
            .ToList();
    }

    // ---------- step-skipped factory -----------------------------------

    private static PlannedStep SkippedStep(DeploymentStepDto step, PlannedStepStatus status, string message) => new()
    {
        StepId = step.Id,
        StepName = step.Name,
        StepOrder = step.StepOrder,
        Status = status,
        StatusMessage = message
    };

    // ---------- global blocker aggregation ------------------------------

    private static void AddGlobalBlockers(
        DeploymentPlanRequest request,
        List<PlannedStep> plannedSteps,
        IReadOnlyList<PlannedTarget> candidateTargets,
        List<PlanBlockingReason> blockers)
    {
        var hasTargetLevelSteps = plannedSteps.Any(s =>
            s.Status is PlannedStepStatus.Applicable or PlannedStepStatus.NoMatchingTargets);

        if (!hasTargetLevelSteps)
            return;

        if (candidateTargets.Count == 0)
            blockers.Add(BuildNoSelectedMachinesBlocker(request));
    }

    // ---------- blocker factories ---------------------------------------

    private static PlanBlockingReason BuildNoMatchingTargetsBlocker(DeploymentStepDto step, List<string> requiredRoles) => new()
    {
        Code = PlanBlockingReasonCodes.NoMatchingTargets,
        Message = BuildNoMatchingTargetsMessage(step, requiredRoles),
        StepId = step.Id,
        StepName = step.Name
    };

    private static string BuildNoMatchingTargetsMessage(DeploymentStepDto step, List<string> requiredRoles)
    {
        if (requiredRoles.Count == 0)
            return $"Step \"{step.Name}\" has no matching targets in the candidate pool.";

        return $"Step \"{step.Name}\" requires roles [{string.Join(", ", requiredRoles)}] but no candidate target has any of them.";
    }

    private static PlanBlockingReason BuildNoSelectedMachinesBlocker(DeploymentPlanRequest request) => new()
    {
        Code = PlanBlockingReasonCodes.NoSelectedMachines,
        Message = $"No available machines in environment {request.EnvironmentId} after applying machine selection."
    };

    private static PlanBlockingReason BuildTransportUnresolvedBlocker(PlannedTarget target) => new()
    {
        Code = PlanBlockingReasonCodes.TransportUnresolved,
        Message = $"Target \"{target.MachineName}\" has no resolved deployment transport.",
        MachineId = target.MachineId,
        MachineName = target.MachineName
    };

    // ---------- blocker dedupe (stable order) ---------------------------

    private static List<PlanBlockingReason> DedupeBlockers(List<PlanBlockingReason> blockers)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PlanBlockingReason>(blockers.Count);

        foreach (var reason in blockers)
        {
            var key = $"{reason.Code}|{reason.StepId}|{reason.MachineId}|{reason.Detail}|{reason.Message}";

            if (seen.Add(key))
                result.Add(reason);
        }

        return result;
    }
}
