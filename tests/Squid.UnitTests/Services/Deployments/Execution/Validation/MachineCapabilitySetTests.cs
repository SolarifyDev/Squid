using System;
using System.Linq;
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

    // ── H7: Installed roles projection ──────────────────────────────────────

    [Theory]
    [InlineData("iis", CapabilityKeys.Role.IIS)]
    [InlineData("docker", CapabilityKeys.Role.Docker)]
    [InlineData("nginx", CapabilityKeys.Role.Nginx)]
    [InlineData("systemd", CapabilityKeys.Role.Systemd)]
    public void ProjectRoles_SingleRole_ProducesPerRoleSlot(string roleName, string expectedSlot)
    {
        // H7 — each agent-detected role becomes its own slot under role:* with
        // value Present. Handler requirements check via the slot name (e.g.
        // role:iis → handler declares CapabilityKeys.Role.IIS).
        var caps = new MachineRuntimeCapabilities { InstalledRoles = roleName };

        var projected = MachineCapabilitySet.From(caps);

        projected.ContainsKey(expectedSlot).ShouldBeTrue();
        projected[expectedSlot].ShouldContain(CapabilityKeys.Present);
    }

    [Fact]
    public void ProjectRoles_MultipleRoles_EachGetsOwnSlot()
    {
        // H7 — comma-separated list (the wire shape used by the agent's
        // RuntimeCapabilitiesInspector.MetaInstalledRoles metadata key)
        // expands to individual slots so handlers can AND-require multiple
        // roles (e.g. a future composite handler that wants both IIS and
        // Docker).
        var caps = new MachineRuntimeCapabilities { InstalledRoles = "iis,docker,nginx" };

        var projected = MachineCapabilitySet.From(caps);

        projected.ContainsKey(CapabilityKeys.Role.IIS).ShouldBeTrue();
        projected.ContainsKey(CapabilityKeys.Role.Docker).ShouldBeTrue();
        projected.ContainsKey(CapabilityKeys.Role.Nginx).ShouldBeTrue();
        projected[CapabilityKeys.Role.IIS].ShouldContain(CapabilityKeys.Present);
        projected[CapabilityKeys.Role.Docker].ShouldContain(CapabilityKeys.Present);
        projected[CapabilityKeys.Role.Nginx].ShouldContain(CapabilityKeys.Present);
    }

    [Fact]
    public void ProjectRoles_EmptyRoles_ProducesNoRoleSlots_OptimisticAllow()
    {
        // H7 backward-compat invariant: pre-H7 agents don't emit installedRoles
        // metadata → projection contains no role:* slots → handler's role
        // requirement (e.g. role:iis on IISDeployActionHandler) falls through
        // to the validator's "absent slot = unknown = optimistic-allow" path.
        // Existing fleets keep working without forcing an agent upgrade first.
        var caps = new MachineRuntimeCapabilities { InstalledRoles = string.Empty };

        var projected = MachineCapabilitySet.From(caps);

        projected.Keys.ShouldNotContain(k => k.StartsWith("role:", StringComparison.OrdinalIgnoreCase),
            customMessage: "Pre-H7 agent (empty InstalledRoles) MUST NOT project any role slots — operators on the old agent keep working with optimistic-allow on the new handler requirement.");
    }

    [Fact]
    public void ProjectRoles_WhitespaceAndDuplicates_NormalisedToLowercase()
    {
        // Defensive: agent might emit " IIS ,  Docker , docker" (whitespace,
        // mixed case, dupes). Projection trims + lowercases + ImmutableDictionary's
        // last-write-wins handles dupes — no exception, sensible output.
        var caps = new MachineRuntimeCapabilities { InstalledRoles = " IIS ,  Docker , docker " };

        var projected = MachineCapabilitySet.From(caps);

        projected.ContainsKey(CapabilityKeys.Role.IIS).ShouldBeTrue();
        projected.ContainsKey(CapabilityKeys.Role.Docker).ShouldBeTrue();
    }
}
