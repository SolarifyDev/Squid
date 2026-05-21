using Shouldly;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Constants;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Execution.Validation;

/// <summary>
/// Pins the projection from <see cref="MachineRuntimeCapabilities"/> (the wire-format
/// the agent reports) into the slot map the validator consumes. The projection is
/// where OS-string tolerance lives (canonical + legacy long form both → <c>os: windows</c>);
/// drift here directly causes a target's plan-time gating to silently shift.
/// </summary>
public class MachineCapabilitySetTests
{
    // ── Empty / null inputs ──────────────────────────────────────────────────

    [Fact]
    public void From_NullCapabilities_ReturnsEmptyMap()
    {
        MachineCapabilitySet.From(null).ShouldBeEmpty();
    }

    [Fact]
    public void From_EmptyCapabilities_ReturnsEmptyMap()
    {
        MachineCapabilitySet.From(MachineRuntimeCapabilities.Empty).ShouldBeEmpty();
    }

    [Fact]
    public void From_AllFieldsBlank_ReturnsEmptyMap()
    {
        var caps = new MachineRuntimeCapabilities { Os = "", Architecture = "", InstalledShells = "" };
        MachineCapabilitySet.From(caps).ShouldBeEmpty();
    }

    // ── OS projection — canonical short form ─────────────────────────────────

    [Theory]
    [InlineData(AgentOperatingSystems.Windows, CapabilityKeys.Os.Windows)]
    [InlineData(AgentOperatingSystems.Linux, CapabilityKeys.Os.Linux)]
    [InlineData(AgentOperatingSystems.MacOS, CapabilityKeys.Os.MacOS)]
    [InlineData("windows", CapabilityKeys.Os.Windows)]   // case-insensitive
    [InlineData("WINDOWS", CapabilityKeys.Os.Windows)]
    [InlineData("LINUX", CapabilityKeys.Os.Linux)]
    public void From_CanonicalOsString_ProjectsToCorrectSlot(string osValue, string expectedSlotValue)
    {
        var caps = new MachineRuntimeCapabilities { Os = osValue };
        var projected = MachineCapabilitySet.From(caps);

        projected.ContainsKey(CapabilityKeys.OsSlot).ShouldBeTrue();
        projected[CapabilityKeys.OsSlot].ShouldContain(expectedSlotValue);
    }

    // ── OS projection — legacy long form tolerance ───────────────────────────

    [Theory]
    [InlineData("Microsoft Windows NT 10.0.19045.0")]    // Win10 22H2
    [InlineData("Microsoft Windows NT 10.0.22631.0")]    // Win11 23H2
    [InlineData("Microsoft Windows NT 10.0.17763.0")]    // Server 2019
    [InlineData("Microsoft Windows NT 10.0.20348.0")]    // Server 2022
    [InlineData("Microsoft Windows NT 6.3.9600.0")]      // Win8.1 / Server 2012 R2
    [InlineData("microsoft windows nt 10.0.19045.0")]    // lowercase
    public void From_LegacyWindowsLongForm_ProjectsToCanonicalWindows(string osValue)
    {
        // Real production failure mode that motivated this projection: older Tentacle
        // binaries wrote Environment.OSVersion.VersionString directly into metadata["os"].
        // Without tolerance, the slot match against a handler that requires
        // {os: windows} would fail and block a fully-functional Windows target.
        var caps = new MachineRuntimeCapabilities { Os = osValue };
        var projected = MachineCapabilitySet.From(caps);

        projected[CapabilityKeys.OsSlot].ShouldContain(CapabilityKeys.Os.Windows,
            customMessage:
                $"Legacy OS string '{osValue}' MUST project to canonical '{CapabilityKeys.Os.Windows}'. " +
                "Drift in MachineCapabilitySet.IsWindows would reintroduce the production bug where " +
                "operators on older Tentacle binaries see their Windows targets rejected at preview time.");
    }

    [Theory]
    [InlineData("LinuxOnWindowsSubsystem")]
    [InlineData("not-a-windows-host")]
    [InlineData("WindowsSomethingElse")]    // doesn't start with "Microsoft Windows"
    public void From_StringMerelyContainingWindows_DoesNotFalsePositive(string osValue)
    {
        // Anchoring guard: the projection uses StartsWith("Microsoft Windows"), not
        // Contains("Windows"), exactly to prevent false-positives like this.
        var caps = new MachineRuntimeCapabilities { Os = osValue };
        var projected = MachineCapabilitySet.From(caps);

        if (projected.TryGetValue(CapabilityKeys.OsSlot, out var slotValues))
            slotValues.ShouldNotContain(CapabilityKeys.Os.Windows,
                customMessage:
                    $"String '{osValue}' merely CONTAINS 'Windows' but isn't a Windows OS marker. " +
                    "False-positive in MachineCapabilitySet.IsWindows would let non-Windows targets " +
                    "pass IIS-deploy gating — a real regression risk.");
    }

    [Fact]
    public void From_UnknownOs_OmitsSlotSoSlotMatchIsOptimistic()
    {
        // "Unknown" is the agent's signal for "I couldn't detect my OS". We omit the
        // slot from the projection — the validator then treats the slot as "target
        // didn't advertise" and falls into optimistic-allow, matching pre-existing
        // IIS handler semantics ("OS cache miss proceeds optimistically").
        var caps = new MachineRuntimeCapabilities { Os = AgentOperatingSystems.Unknown };
        var projected = MachineCapabilitySet.From(caps);

        projected.ContainsKey(CapabilityKeys.OsSlot).ShouldBeFalse();
    }

    // ── Architecture projection ──────────────────────────────────────────────

    [Theory]
    [InlineData("X64", "x64")]
    [InlineData("Arm64", "arm64")]
    [InlineData("X86", "x86")]
    [InlineData("x64", "x64")]
    public void From_Architecture_NormalizedToLowercase(string archInput, string expected)
    {
        var caps = new MachineRuntimeCapabilities { Architecture = archInput };
        var projected = MachineCapabilitySet.From(caps);

        projected[CapabilityKeys.ArchSlot].ShouldContain(expected);
    }

    // ── Installed shells projection ──────────────────────────────────────────

    [Fact]
    public void From_InstalledShells_EachShellGetsOwnSlot()
    {
        // Each shell becomes its OWN slot — so a handler can AND-require multiple
        // shells (e.g. "needs both pwsh AND bash") by declaring both slots.
        var caps = new MachineRuntimeCapabilities { InstalledShells = "pwsh,powershell,cmd" };
        var projected = MachineCapabilitySet.From(caps);

        projected.ContainsKey(CapabilityKeys.Shell.Pwsh).ShouldBeTrue();
        projected.ContainsKey(CapabilityKeys.Shell.PowerShell).ShouldBeTrue();
        projected.ContainsKey(CapabilityKeys.Shell.Cmd).ShouldBeTrue();
        projected[CapabilityKeys.Shell.Pwsh].ShouldContain(CapabilityKeys.Present);
    }

    [Fact]
    public void From_InstalledShellsWithSpaces_AreTrimmed()
    {
        var caps = new MachineRuntimeCapabilities { InstalledShells = "pwsh, bash , sh" };
        var projected = MachineCapabilitySet.From(caps);

        projected.ContainsKey(CapabilityKeys.Shell.Pwsh).ShouldBeTrue();
        projected.ContainsKey(CapabilityKeys.Shell.Bash).ShouldBeTrue();
        projected.ContainsKey(CapabilityKeys.Shell.Sh).ShouldBeTrue();
    }

    [Fact]
    public void From_InstalledShellsMixedCase_NormalizedToLowercase()
    {
        var caps = new MachineRuntimeCapabilities { InstalledShells = "PWSH,PowerShell" };
        var projected = MachineCapabilitySet.From(caps);

        projected.ContainsKey("shell:pwsh").ShouldBeTrue();
        projected.ContainsKey("shell:powershell").ShouldBeTrue();
    }

    // ── Full projection composition ──────────────────────────────────────────

    [Fact]
    public void From_FullCapabilities_ProducesAllSlots()
    {
        var caps = new MachineRuntimeCapabilities
        {
            Os = AgentOperatingSystems.Windows,
            Architecture = "X64",
            InstalledShells = "pwsh,powershell"
        };

        var projected = MachineCapabilitySet.From(caps);

        projected[CapabilityKeys.OsSlot].ShouldContain(CapabilityKeys.Os.Windows);
        projected[CapabilityKeys.ArchSlot].ShouldContain(CapabilityKeys.Arch.X64);
        projected[CapabilityKeys.Shell.Pwsh].ShouldContain(CapabilityKeys.Present);
        projected[CapabilityKeys.Shell.PowerShell].ShouldContain(CapabilityKeys.Present);
    }
}
