using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Variables;

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
///   Action      = 10,000,000
///   Machine     =  1,000,000
///   Role        =     10,000
///   Process     =      1,000
///   Environment =        100
///   Channel     =         10
/// </summary>
public static class VariableScopeEvaluator
{
    private const int ChannelWeight = 10;
    private const int EnvironmentWeight = 100;
    private const int ProcessWeight = 1_000;
    private const int RoleWeight = 10_000;
    private const int MachineWeight = 1_000_000;
    private const int ActionWeight = 10_000_000;

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
                VariableScopeType.Process => ProcessWeight,
                VariableScopeType.Role => RoleWeight,
                VariableScopeType.Machine => MachineWeight,
                VariableScopeType.Action => ActionWeight,
                _ => 0
            };
        }

        return rank;
    }

    private static bool MatchesScopeValue(VariableScopeType scopeType, string scopeValue, VariableScopeContext context)
    {
        return scopeType switch
        {
            VariableScopeType.Environment => MatchesIdOrName(scopeValue, context.EnvironmentId, context.EnvironmentName),
            VariableScopeType.Machine => MatchesIdOrName(scopeValue, context.MachineId, context.MachineName),
            VariableScopeType.Channel => MatchesIdOrName(scopeValue, context.ChannelId, context.ChannelName),
            VariableScopeType.Action => MatchesIdOrName(scopeValue, context.ActionId, context.ActionName),
            VariableScopeType.Process => MatchesIdOrName(scopeValue, context.ProcessId, context.ProcessName),
            VariableScopeType.Role => context.Roles is { Count: > 0 } && context.Roles.Contains(scopeValue),
            _ => true
        };
    }

    private static bool MatchesIdOrName(string scopeValue, int? id, string name)
    {
        if (string.IsNullOrEmpty(scopeValue)) return false;

        if (id.HasValue && string.Equals(scopeValue, id.Value.ToString(), StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(name) && string.Equals(scopeValue, name, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}

public record VariableScopeContext
{
    public int? EnvironmentId { get; init; }
    public string EnvironmentName { get; init; }

    public int? MachineId { get; init; }
    public string MachineName { get; init; }

    public HashSet<string> Roles { get; init; }

    public int? ChannelId { get; init; }
    public string ChannelName { get; init; }

    public int? ActionId { get; init; }
    public string ActionName { get; init; }

    public int? ProcessId { get; init; }
    public string ProcessName { get; init; }
}
