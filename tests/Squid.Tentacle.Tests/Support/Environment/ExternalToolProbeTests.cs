using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Support.Environment;

[Trait("Category", TentacleTestCategories.Core)]
public class ExternalToolProbeTests
{
    [Fact]
    public void Probes_Return_Boolean_Without_Throwing()
    {
        var _ = ExternalToolProbe.HasHelm();
        _ = ExternalToolProbe.HasKubectl();
        _ = ExternalToolProbe.HasKind();
    }
}
