using Shouldly;
using Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;
using Squid.Core.Services.DeploymentExecution.Validation;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Execution.Validation;

/// <summary>
/// Pins the single source of truth for "is this OS string a Windows host?".
/// Before <see cref="WindowsOsStringHelper"/> existed, the tolerance logic was
/// bit-copied into both <c>IISDeployActionHandler.LooksLikeWindowsOsString</c>
/// (dispatch-time guard) and <c>MachineCapabilitySet.IsWindows</c> (plan-time
/// projection). Drift between the two copies would have shifted what counts as
/// a Windows target without anyone noticing.
///
/// <para><b>Cross-call-site drift detector</b>: the two preserved backward-
/// compatible wrappers (<c>LooksLikeWindowsOsString</c>,
/// <c>MachineCapabilitySet.IsWindows</c>) MUST forward to this helper, not
/// reimplement the logic. The
/// <c>BackwardCompatibleWrappers_DelegateToHelper</c> test verifies all three
/// surfaces return identical results for the same input — if they ever diverge,
/// someone reintroduced the duplication and the test catches it immediately.</para>
/// </summary>
public class WindowsOsStringHelperTests
{
    // ── Canonical short form ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Windows", true)]
    [InlineData("windows", true)]
    [InlineData("WINDOWS", true)]
    [InlineData("WindowS", true)]
    public void IsWindows_CanonicalShortForm_AcceptedCaseInsensitive(string input, bool expected)
        => WindowsOsStringHelper.IsWindows(input).ShouldBe(expected);

    // ── Legacy long form — real Windows version strings ──────────────────────

    [Theory]
    [InlineData("Microsoft Windows NT 10.0.19045.0")]    // Win10 22H2 (operator's failure mode)
    [InlineData("Microsoft Windows NT 10.0.22631.0")]    // Win11 23H2
    [InlineData("Microsoft Windows NT 10.0.17763.0")]    // Server 2019
    [InlineData("Microsoft Windows NT 10.0.20348.0")]    // Server 2022
    [InlineData("Microsoft Windows NT 6.3.9600.0")]      // Server 2012 R2
    [InlineData("Microsoft Windows NT 6.1.7601.0")]      // Win7 SP1
    [InlineData("microsoft windows nt 10.0.19045.0")]    // lowercase
    [InlineData("MICROSOFT WINDOWS NT 10.0.19045.0")]    // uppercase
    public void IsWindows_LegacyLongForm_AcceptedCaseInsensitive(string input)
    {
        WindowsOsStringHelper.IsWindows(input).ShouldBeTrue(
            customMessage:
                $"Legacy OS string '{input}' MUST be recognised as Windows. " +
                "Drift here would reintroduce the production bug where operators on older Tentacle " +
                "binaries see Windows targets rejected at preview time.");
    }

    // ── Non-Windows OS markers ───────────────────────────────────────────────

    [Theory]
    [InlineData("Linux")]
    [InlineData("linux")]
    [InlineData("macOS")]
    [InlineData("Darwin")]
    [InlineData("FreeBSD")]
    [InlineData("Unknown")]
    public void IsWindows_NonWindowsMarkers_Rejected(string input)
        => WindowsOsStringHelper.IsWindows(input).ShouldBeFalse();

    // ── Anti-false-positive anchor (the critical guard) ──────────────────────

    [Theory]
    [InlineData("LinuxOnWindowsSubsystem")]          // Contains "Windows" but isn't Windows
    [InlineData("not-a-windows-host")]
    [InlineData("WindowsSomethingElse")]             // Doesn't start with "Microsoft Windows"
    [InlineData("Some-Microsoft-Windows-mirror")]    // Doesn't START with the prefix
    [InlineData("Windowsy")]                         // Almost the canonical form but not quite
    public void IsWindows_StringMerelyContainingWindows_DoesNotFalsePositive(string input)
    {
        WindowsOsStringHelper.IsWindows(input).ShouldBeFalse(
            customMessage:
                $"String '{input}' merely CONTAINS 'Windows' but isn't a Windows OS marker. " +
                "False-positive here would let non-Windows targets pass IIS-deploy gating — a real " +
                "regression risk. The anchor MUST be StartsWith(\"Microsoft Windows\"), NOT Contains(\"Windows\").");
    }

    // ── Edge cases ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void IsWindows_NullOrBlank_Rejected(string input)
        => WindowsOsStringHelper.IsWindows(input).ShouldBeFalse();

    // ── Backward-compatible delegation pin (the drift detector) ──────────────

    [Theory]
    [InlineData("Windows")]
    [InlineData("Microsoft Windows NT 10.0.19045.0")]
    [InlineData("Linux")]
    [InlineData("LinuxOnWindowsSubsystem")]
    [InlineData(null)]
    [InlineData("")]
    public void BackwardCompatibleWrappers_DelegateToHelper(string input)
    {
        // Three call sites that historically had their own copy of the logic.
        // After PR consolidating to WindowsOsStringHelper, the two old surfaces
        // are kept as backward-compat wrappers. They MUST forward to the helper —
        // if they reintroduce their own logic, this test catches the drift on
        // the first divergent input. Covered inputs span every branch (short,
        // long, non-Windows, false-positive guard, null, empty).
        var expected = WindowsOsStringHelper.IsWindows(input);

        IISDeployActionHandler.LooksLikeWindowsOsString(input).ShouldBe(expected,
            customMessage:
                $"IISDeployActionHandler.LooksLikeWindowsOsString('{input}') diverged from " +
                "WindowsOsStringHelper.IsWindows. Someone reimplemented the logic locally — " +
                "delete the local copy and delegate to the helper.");

        MachineCapabilitySet.IsWindows(input).ShouldBe(expected,
            customMessage:
                $"MachineCapabilitySet.IsWindows('{input}') diverged from " +
                "WindowsOsStringHelper.IsWindows. Same drift issue — delegate to the helper.");
    }
}
