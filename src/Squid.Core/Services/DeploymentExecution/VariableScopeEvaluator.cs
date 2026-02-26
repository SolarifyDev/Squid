using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

/// <summary>
/// Filters and resolves scoped variables following Octopus-aligned precedence rules.
///
/// Semantics:
/// - Within a scope type (e.g. multiple Environment scopes): OR — any value match suffices.
/// - Across scope types (e.g. Environment + Machine): AND — all types must match.
/// - Unscoped variables apply everywhere but have lowest priority (rank 0).
/// - When multiple variables share the same name, the highest-ranked (most specific) wins.
///
/// Rank weights (aligned with Octopus ScopeSpecification.Rank()):
///   Machine     = 1,000,000
///   Role        =    10,000
///   Channel     =        10
///   Environment =       100
/// </summary>
public static class VariableScopeEvaluator
{
    private const int ChannelWeight = 10;
    private const int EnvironmentWeight = 100;
    private const int RoleWeight = 10_000;
    private const int MachineWeight = 1_000_000;

    public static List<VariableDto> Evaluate(List<VariableDto> variables, VariableScopeContext scopeContext)
    {
        if (variables == null || variables.Count == 0)
            return new List<VariableDto>();

        return variables
            .Where(v => IsApplicable(v, scopeContext))
            .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(ComputeRank).First())
            .ToList();
    }

    public static bool IsApplicable(VariableDto variable, VariableScopeContext context)
    {
        if (variable.Scopes == null || variable.Scopes.Count == 0)
            return true;

        var scopesByType = variable.Scopes.GroupBy(s => s.ScopeType);

        foreach (var typeGroup in scopesByType)
        {
            if (!typeGroup.Any(scope => MatchesScopeValue(scope.ScopeType, scope.ScopeValue, context)))
                return false;
        }

        return true;
    }

    public static int ComputeRank(VariableDto variable)
    {
        if (variable.Scopes == null || variable.Scopes.Count == 0)
            return 0;

        var rank = 0;
        var scopeTypes = variable.Scopes.Select(s => s.ScopeType).Distinct();

        foreach (var scopeType in scopeTypes)
        {
            rank += scopeType switch
            {
                VariableScopeType.Channel => ChannelWeight,
                VariableScopeType.Environment => EnvironmentWeight,
                VariableScopeType.Role => RoleWeight,
                VariableScopeType.Machine => MachineWeight,
                _ => 0
            };
        }

        return rank;
    }

    private static bool MatchesScopeValue(VariableScopeType scopeType, string scopeValue, VariableScopeContext context)
    {
        return scopeType switch
        {
            VariableScopeType.Environment => context.EnvironmentId.HasValue
                && string.Equals(scopeValue, context.EnvironmentId.Value.ToString(), StringComparison.OrdinalIgnoreCase),

            VariableScopeType.Machine => context.MachineId.HasValue
                && string.Equals(scopeValue, context.MachineId.Value.ToString(), StringComparison.OrdinalIgnoreCase),

            VariableScopeType.Role => context.Roles is { Count: > 0 }
                && context.Roles.Contains(scopeValue),

            VariableScopeType.Channel => context.ChannelId.HasValue
                && string.Equals(scopeValue, context.ChannelId.Value.ToString(), StringComparison.OrdinalIgnoreCase),

            _ => true
        };
    }
}

public class VariableScopeContext
{
    public int? EnvironmentId { get; init; }

    public int? MachineId { get; init; }

    public HashSet<string> Roles { get; init; }

    public int? ChannelId { get; init; }
}
