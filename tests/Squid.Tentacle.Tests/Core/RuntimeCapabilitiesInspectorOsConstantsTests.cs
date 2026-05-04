using Squid.Message.Constants;
using Squid.Tentacle.Core;

namespace Squid.Tentacle.Tests.Core;

/// <summary>
/// P1-Phase12.E.5 — agent half of the cross-process OS-string contract pin.
/// The server side mirrors this with
/// <c>MachineRuntimeCapabilitiesOsConstantsTests</c> in
/// <c>Squid.UnitTests</c>. Both halves must pin the SAME literal values
/// because the agent writes them and the server reads them — drift on
/// either side silently breaks <c>IsWindows / IsLinux / IsMacOS / IsUnknown</c>
/// routing and the <c>WindowsTentacleUpgradeStrategy</c> / version-registry
/// dispatch.
///
/// <para><b>Why an agent-side test in addition to the server-side one</b>:
/// they live in different assemblies (<c>Squid.Message</c> hosts the
/// constants but <c>Squid.Tentacle</c> consumes them via
/// <c>RuntimeCapabilitiesInspector.DetectOs</c>; <c>Squid.Core</c> consumes
/// them via <c>MachineRuntimeCapabilities.IsXxx</c>). A constant rename
/// would break both consumers, but the test on each side gives a clearer
/// signal of WHICH consumer regressed.</para>
/// </summary>
public class RuntimeCapabilitiesInspectorOsConstantsTests
{
    [Fact]
    public void Inspector_ReportsCanonicalOsString()
    {
        // Run the live inspector on the test host. The result is the canonical
        // string for whichever OS the test is running on. We pin that the
        // result matches one of the four AgentOperatingSystems consts — drift
        // (e.g. inspector returns lowercased "windows" while the const is
        // "Windows") would be caught here.
        var metadata = RuntimeCapabilitiesInspector.Inspect();

        metadata.ShouldContainKey(RuntimeCapabilitiesInspector.MetaOs);
        var reportedOs = metadata[RuntimeCapabilitiesInspector.MetaOs];

        reportedOs.ShouldBeOneOf(
            AgentOperatingSystems.Windows,
            AgentOperatingSystems.Linux,
            AgentOperatingSystems.MacOS,
            AgentOperatingSystems.Unknown);
    }

    [Fact]
    public void AgentOperatingSystems_Windows_PinnedFromAgentSide()
        => AgentOperatingSystems.Windows.ShouldBe("Windows");

    [Fact]
    public void AgentOperatingSystems_Linux_PinnedFromAgentSide()
        => AgentOperatingSystems.Linux.ShouldBe("Linux");

    [Fact]
    public void AgentOperatingSystems_MacOS_PinnedFromAgentSide()
        => AgentOperatingSystems.MacOS.ShouldBe("macOS");

    [Fact]
    public void AgentOperatingSystems_Unknown_PinnedFromAgentSide()
        => AgentOperatingSystems.Unknown.ShouldBe("Unknown");
}
