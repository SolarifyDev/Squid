using Squid.Tentacle.Core;

namespace Squid.Tentacle.Tests.Core;

/// <summary>
/// Unit tests for the SCM-detection seam in <see cref="TentacleEntry.ShouldRunUnderScm"/>.
/// Pinned because the seam is the entry-point for the Windows-service
/// lifetime branch — a regression here means SCM-launched start either
/// times out (false negative: should have detected SCM but didn't) or
/// triggers the service lifetime when it shouldn't (false positive:
/// console-launched register / show-thumbprint hangs trying to talk
/// to a non-existent SCM).
///
/// <para>The seam takes a <c>Func&lt;bool&gt;</c> for the SCM-launched
/// check so tests can run on any platform without needing a real SCM
/// (the production wiring passes <c>WindowsServiceHelpers.IsWindowsService</c>).</para>
///
/// <para>Cross-platform: these tests run identically on Windows / Linux /
/// macOS. No skip-on-OS guards needed.</para>
/// </summary>
public sealed class TentacleEntryTests
{
    // ========================================================================
    // ShouldRunUnderScm — false on non-Windows regardless of other inputs
    //
    // SCM is a Windows-only concept. Even if the seam's "is launched by
    // SCM" predicate erroneously returned true on Linux/macOS (which it
    // can't in production — WindowsServiceHelpers.IsWindowsService folds
    // the Windows check in), we MUST NOT enter SCM mode. A regression
    // here would crash AddWindowsService() at host startup on non-
    // Windows because WindowsServiceLifetime has [SupportedOSPlatform("windows")].
    // ========================================================================

    [Fact]
    public void ShouldRunUnderScm_NonWindows_ReturnsFalse_RegardlessOfScmPredicate()
    {
        var args = new[] { "run" };

        // Even if the SCM predicate erroneously returns true, we're not
        // on Windows → must short-circuit to false.
        var result = TentacleEntry.ShouldRunUnderScm(args, isWindows: false, isLaunchedBySCM: () => true);

        result.ShouldBeFalse(
            customMessage: "ShouldRunUnderScm MUST return false on non-Windows even when the SCM predicate returns true. " +
                          "SCM is a Windows-only concept; entering SCM mode on Linux/macOS would crash WindowsServiceLifetime " +
                          "(which is [SupportedOSPlatform(\"windows\")]).");
    }

    // ========================================================================
    // ShouldRunUnderScm — false when not launched by SCM (interactive console)
    //
    // The most common case: operator runs `Squid.Tentacle.exe register ...`
    // from PowerShell or CMD. Even on Windows, isLaunchedBySCM=false →
    // return false → use console flow.
    // ========================================================================

    [Fact]
    public void ShouldRunUnderScm_WindowsConsole_NotLaunchedBySCM_ReturnsFalse()
    {
        var args = new[] { "run" };

        var result = TentacleEntry.ShouldRunUnderScm(args, isWindows: true, isLaunchedBySCM: () => false);

        result.ShouldBeFalse(
            customMessage: "ShouldRunUnderScm MUST return false on Windows when NOT launched by SCM. " +
                          "Interactive `Squid.Tentacle.exe run` from a terminal must use the console flow, " +
                          "not the Windows-service host pipeline (which would fail at startup with a " +
                          "StartServiceCtrlDispatcher error since there's no SCM connection).");
    }

    // ========================================================================
    // ShouldRunUnderScm — false when SCM-launched but not the `run` command
    //
    // SCM only ever launches the binary's `run` command — that's what
    // ServiceCommand registers in the SCM binPath. But for defense in
    // depth (e.g. an operator manually configures SCM to run a different
    // command), the seam short-circuits non-`run` commands so we don't
    // wrap short-lived CLI commands in a host lifetime.
    // ========================================================================

    [Theory]
    [InlineData("register")]
    [InlineData("show-thumbprint")]
    [InlineData("show-config")]
    [InlineData("list-instances")]
    [InlineData("create-instance")]
    [InlineData("delete-instance")]
    [InlineData("new-certificate")]
    [InlineData("version")]
    [InlineData("service")]      // service install/uninstall/start/stop
    public void ShouldRunUnderScm_SCMLaunched_ButNotRunCommand_ReturnsFalse(string command)
    {
        var args = new[] { command };

        var result = TentacleEntry.ShouldRunUnderScm(args, isWindows: true, isLaunchedBySCM: () => true);

        result.ShouldBeFalse(
            customMessage: $"ShouldRunUnderScm MUST return false for '{command}' even when SCM-launched. " +
                          "Only the long-running `run` command needs WindowsServiceLifetime — short-lived " +
                          $"commands like {command} would have their CLI summary output truncated by the " +
                          "host's logging pipeline OR their exit code lost in the host's shutdown sequence.");
    }

    // ========================================================================
    // ShouldRunUnderScm — TRUE when all three conditions are met
    //
    // The happy path: Windows + SCM-launched + `run` command → enter SCM
    // mode. This is what enables `sc start squid-tentacle` to actually
    // reach RUNNING state (vs the pre-fix ERROR_SERVICE_REQUEST_TIMEOUT).
    // ========================================================================

    [Fact]
    public void ShouldRunUnderScm_WindowsSCMLaunchedRunCommand_ReturnsTrue()
    {
        var args = new[] { "run" };

        var result = TentacleEntry.ShouldRunUnderScm(args, isWindows: true, isLaunchedBySCM: () => true);

        result.ShouldBeTrue(
            customMessage: "ShouldRunUnderScm MUST return true on Windows when SCM-launched + `run`. " +
                          "Without this, `sc start squid-tentacle` times out at ERROR_SERVICE_REQUEST_TIMEOUT " +
                          "after 30s because the binary never registers a service control handler.");
    }

    // ========================================================================
    // ShouldRunUnderScm — TRUE for `run --instance NAME` (named instance)
    //
    // Multi-instance setups install separate SCM entries per instance
    // (squid-tentacle, squid-tentacle-staging, etc.) where each binPath
    // is `Squid.Tentacle.exe run --instance <name>`. The detection seam
    // MUST handle args beyond just `run` correctly.
    // ========================================================================

    [Fact]
    public void ShouldRunUnderScm_RunWithInstanceArg_ReturnsTrue()
    {
        var args = new[] { "run", "--instance", "production" };

        var result = TentacleEntry.ShouldRunUnderScm(args, isWindows: true, isLaunchedBySCM: () => true);

        result.ShouldBeTrue(
            customMessage: "ShouldRunUnderScm MUST handle `run --instance NAME` (multi-instance SCM setups). " +
                          "If false: CommandResolver doesn't recognize `run` when followed by additional args, " +
                          "OR the seam's predicate logic short-circuited incorrectly.");
    }

    // ========================================================================
    // ShouldRunUnderScm — argument validation (null arrays / null predicate)
    //
    // Defensive: nulls should produce ArgumentNullException with the
    // parameter name, not crash with NullReferenceException deep in
    // CommandResolver.
    // ========================================================================

    [Fact]
    public void ShouldRunUnderScm_NullArgs_Throws()
    {
        Action act = () => TentacleEntry.ShouldRunUnderScm(args: null, isWindows: true, isLaunchedBySCM: () => true);

        act.ShouldThrow<ArgumentNullException>().ParamName.ShouldBe("args");
    }

    [Fact]
    public void ShouldRunUnderScm_NullPredicate_Throws()
    {
        Action act = () => TentacleEntry.ShouldRunUnderScm(args: new[] { "run" }, isWindows: true, isLaunchedBySCM: null);

        act.ShouldThrow<ArgumentNullException>().ParamName.ShouldBe("isLaunchedBySCM");
    }

    // ========================================================================
    // ShouldRunUnderScm — predicate is NOT called when isWindows is false
    //
    // Performance + defense: on non-Windows we should short-circuit
    // before calling the predicate (which on production wraps a P/Invoke
    // to GetCurrentProcess + parent-process check — no need to fire on
    // Linux/macOS where the answer is structurally false anyway).
    // ========================================================================

    [Fact]
    public void ShouldRunUnderScm_NonWindows_DoesNotInvokePredicate()
    {
        var predicateCalled = false;

        var result = TentacleEntry.ShouldRunUnderScm(
            args: new[] { "run" },
            isWindows: false,
            isLaunchedBySCM: () =>
            {
                predicateCalled = true;
                return true;
            });

        result.ShouldBeFalse();
        predicateCalled.ShouldBeFalse(
            customMessage: "predicate MUST NOT be invoked on non-Windows — short-circuit before the call. " +
                          "If invoked: production binary on Linux is doing a needless P/Invoke per startup " +
                          "(WindowsServiceHelpers.IsWindowsService internally checks process handles).");
    }
}
