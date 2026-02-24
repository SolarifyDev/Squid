using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Environment;

namespace Squid.Tentacle.Tests.Integration;

// Base class for future real-process / real-Halibut integration suites.
// Mirrors Octopus' IntegrationTest layering by centralizing prereq checks and timeouts.
[Trait("Category", TentacleTestCategories.Integration)]
public abstract class TentacleIntegrationTestBase : TimedTestBase
{
    protected TentacleIntegrationTestBase(TimeSpan? timeout = null) : base(timeout)
    {
    }

    protected static bool HasHelm() => ExternalToolProbe.HasHelm();
    protected static bool HasKubectl() => ExternalToolProbe.HasKubectl();
    protected static bool HasKind() => ExternalToolProbe.HasKind();
}
