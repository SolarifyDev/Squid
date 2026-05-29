using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

/// <summary>
/// Single source of truth for "which targets does a step run on?" — the
/// step-role → machine match used by BOTH the planner (preview) and the
/// executor's runtime target resolution. Having one implementation guarantees
/// the machines a preview reports as matched are EXACTLY the machines the
/// deployment actually runs on; there is no second, drift-prone matcher.
///
/// <para><b>Rules</b>: a step's required roles come from its
/// <see cref="SpecialVariables.Step.TargetRoles"/> property (comma-separated).
/// An absent / blank / whitespace-only value means the step is unscoped and
/// matches every candidate target. Otherwise a target matches when its roles
/// overlap the required roles (case-insensitive).</para>
/// </summary>
public static class StepRoleMatcher
{
    /// <summary>
    /// The roles a step requires of a target, ordered + case-insensitive.
    /// Empty means "unscoped — matches all targets".
    /// </summary>
    public static IReadOnlyList<string> RequiredRoles(DeploymentStepDto step)
    {
        var rolesProperty = step.Properties?
            .FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.TargetRoles);

        if (rolesProperty == null || string.IsNullOrWhiteSpace(rolesProperty.PropertyValue))
            return Array.Empty<string>();

        return DeploymentTargetFinder.ParseCsvRoles(rolesProperty.PropertyValue)
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Whether a target whose roles are <paramref name="machineRoles"/> matches a
    /// step requiring <paramref name="requiredRoles"/>. Empty required roles match
    /// everything; otherwise the sets must overlap (case-insensitive).
    /// </summary>
    public static bool Matches(IReadOnlyCollection<string> requiredRoles, IEnumerable<string> machineRoles)
    {
        if (requiredRoles.Count == 0) return true;

        var roleSet = new HashSet<string>(requiredRoles, StringComparer.OrdinalIgnoreCase);

        return machineRoles.Any(roleSet.Contains);
    }
}
