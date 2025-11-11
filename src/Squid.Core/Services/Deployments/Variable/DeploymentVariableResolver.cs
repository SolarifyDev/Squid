using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.Deployments.Release;

namespace Squid.Core.Services.Deployments.Variable;

public class ScopeContext
{
    public string EnvironmentId { get; set; }
    public string MachineId { get; set; }
    public string ChannelId { get; set; }
    public Dictionary<string, string> AdditionalScopes { get; set; } = new Dictionary<string, string>();
}

public class ResolvedVariables
{
    public Dictionary<string, string> Variables { get; }
    public List<string> SensitiveVariableNames { get; }

    public ResolvedVariables(Dictionary<string, string> variables, List<string> sensitiveVariableNames = null)
    {
        Variables = variables ?? new Dictionary<string, string>();
        SensitiveVariableNames = sensitiveVariableNames ?? new List<string>();
    }
}

public interface IDeploymentVariableResolver : IScopedDependency
{
    Task<ResolvedVariables> ResolveVariablesForDeploymentAsync(int releaseId, ScopeContext scopeContext, CancellationToken cancellationToken = default);
}

public class DeploymentVariableResolver : IDeploymentVariableResolver
{
    private readonly IHybridVariableSnapshotService _snapshotService;
    private readonly IReleaseDataProvider _releaseDataProvider;

    public DeploymentVariableResolver(
        IHybridVariableSnapshotService snapshotService,
        IReleaseDataProvider releaseDataProvider)
    {
        _snapshotService = snapshotService;
        _releaseDataProvider = releaseDataProvider;
    }

    public async Task<ResolvedVariables> ResolveVariablesForDeploymentAsync(
        int releaseId,
        ScopeContext scopeContext,
        CancellationToken cancellationToken = default)
    {
        Log.Information("Resolving variables for Release {ReleaseId}", releaseId);

        var sensitiveVariableNames = new List<string>();
        var allVariables = new List<VariableSnapshotData>();

        // 获取Release信息
        var release = await _releaseDataProvider.GetReleaseByIdAsync(releaseId, cancellationToken).ConfigureAwait(false);
        if (release == null)
        {
            Log.Warning("Release {ReleaseId} not found", releaseId);
            return new ResolvedVariables(new Dictionary<string, string>(), new List<string>());
        }

        // 从Release的ProjectVariableSetSnapshotId加载变量快照
        if (release.ProjectVariableSetSnapshotId > 0)
        {
            try
            {
                var variableSnapshot = await _snapshotService.LoadSnapshotAsync(release.ProjectVariableSetSnapshotId, cancellationToken).ConfigureAwait(false);
                allVariables.AddRange(variableSnapshot.Variables);

                Log.Information("Loaded {VariableCount} variables from snapshot {SnapshotId}",
                    variableSnapshot.Variables.Count, release.ProjectVariableSetSnapshotId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load variable snapshot {SnapshotId} for Release {ReleaseId}",
                    release.ProjectVariableSetSnapshotId, releaseId);
            }
        }

        var resolvedVariables = new Dictionary<string, string>();

        var sortedVariables = allVariables
            .Where(v => IsVariableInScope(v, scopeContext))
            .OrderBy(v => v.SortOrder)
            .ThenBy(v => GetScopePriority(v, scopeContext))
            .ThenBy(v => v.Name);

        foreach (var variable in sortedVariables)
        {
            resolvedVariables[variable.Name] = variable.Value;

            if (variable.IsSensitive)
            {
                sensitiveVariableNames.Add(variable.Name);
            }
        }

        Log.Information(
            "Resolved {VariableCount} variables for Release {ReleaseId}, including {SensitiveCount} sensitive variables",
            resolvedVariables.Count, releaseId, sensitiveVariableNames.Count);

        return new ResolvedVariables(resolvedVariables, sensitiveVariableNames);
    }

    private bool IsVariableInScope(VariableSnapshotData variable, ScopeContext context)
    {
        if (!variable.Scopes.Any())
            return true;

        var scopeGroups = variable.Scopes.GroupBy(s => s.ScopeType);

        foreach (var scopeGroup in scopeGroups)
        {
            var scopeType = scopeGroup.Key;
            var scopeValues = scopeGroup.Select(s => s.ScopeValue).ToList();

            if (!IsContextMatchingScope(context, scopeType, scopeValues))
                return false;
        }

        return true;
    }

    private bool IsContextMatchingScope(ScopeContext context, VariableScopeType scopeType, List<string> scopeValues)
    {
        return scopeType switch
        {
            VariableScopeType.Environment => scopeValues.Contains(context.EnvironmentId),
            VariableScopeType.Machine => scopeValues.Contains(context.MachineId),
            VariableScopeType.Channel => scopeValues.Contains(context.ChannelId),
            _ => context.AdditionalScopes.TryGetValue(scopeType.ToString(), out var value) && scopeValues.Contains(value)
        };
    }

    private int GetScopePriority(VariableSnapshotData variable, ScopeContext context)
    {
        if (!variable.Scopes.Any())
            return 1000;

        var scopeCount = variable.Scopes.Count;
        var matchingScopes = variable.Scopes.Count(scope => 
            IsContextMatchingScope(context, scope.ScopeType, new List<string> { scope.ScopeValue }));

        return 1000 - (matchingScopes * 100) - scopeCount;
    }
}
