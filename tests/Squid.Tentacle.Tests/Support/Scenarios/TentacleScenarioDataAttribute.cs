using Xunit.Sdk;

namespace Squid.Tentacle.Tests.Support.Scenarios;

public enum TentacleScenarioSet
{
    KubernetesAgentRuntimeSmoke,
    KubernetesAgentCluster
}

public sealed class TentacleScenarioDataAttribute : DataAttribute
{
    private readonly TentacleScenarioSet _scenarioSet;

    public TentacleScenarioDataAttribute(TentacleScenarioSet scenarioSet)
    {
        _scenarioSet = scenarioSet;
    }

    public override IEnumerable<object[]> GetData(System.Reflection.MethodInfo testMethod)
    {
        IEnumerable<TentacleScenarioCase> cases = _scenarioSet switch
        {
            TentacleScenarioSet.KubernetesAgentRuntimeSmoke => TentacleScenarioMatrix.KubernetesAgentRuntimeSmoke(),
            TentacleScenarioSet.KubernetesAgentCluster => TentacleScenarioMatrix.KubernetesAgentClusterScenarios(),
            _ => throw new ArgumentOutOfRangeException()
        };

        return cases.Select(c => new object[] { c });
    }
}
