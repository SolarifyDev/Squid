using Squid.Tentacle.Tests.Integration;
using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Kubernetes.Integration;

[Trait("Category", TentacleTestCategories.Kubernetes)]
public abstract class KubernetesAgentIntegrationTestBase : TentacleIntegrationTestBase
{
    protected bool HasKubernetesToolchain()
    {
        return HasKind() && HasHelm() && HasKubectl();
    }
}
