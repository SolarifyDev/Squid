using System.Diagnostics;
using System.Reflection;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Phase 12.K — E2E coverage for <c>deploy/scripts/install-tentacle.ps1</c>.
/// Drives the real script via <c>powershell.exe -File</c> against a
/// <see cref="LocalReleaseMirror"/> serving fake zip downloads.
///
/// <para><b>Tier</b>: 🟢 High-fidelity (Rule 12). Real powershell.exe
/// running real install script, real ZipFile extraction into a real install
/// directory, real Test-Path verification. Only the upstream release CDN
/// is replaced.</para>
///
/// <para><b>Skip-on-non-Windows</b>: install-tentacle.ps1 uses
/// <c>New-NetFirewallRule</c>, directory junctions, and <c>powershell.exe</c>
/// — all Windows-specific. Tests no-op cleanly on macOS / Linux.</para>
///
/// <para><b>What this catches</b>: a regression in the script's URL
/// composition, arg parsing, fallback URL logic, extraction,
/// or post-extract verification. Every install-tentacle.ps1 release
/// hereafter MUST keep these tests green.</para>
///
/// <para><b>Scenario coverage</b> (per <c>docs/e2e-scenario-matrix.md</c>
/// Section A — Phase 12.K first cut):</para>
/// <list type="bullet">
///   <item>A1.h — happy path with custom install dir + DOWNLOAD_BASE override</item>
///   <item>A1.u1 — bogus version → mirror returns 404 → script exits non-zero</item>
///   <item>A7.h — `-NoServiceInstall` flag skips the service-install step</item>
///   <item>A8.h — re-running over existing install succeeds (idempotent)</item>
/// </list>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleInstallScript)]
public sealed class TentacleInstallScriptWindowsE2ETests : IDisposable
{
    // Test-isolated %ProgramData% so install-tentacle.ps1's install-info.json
    // write doesn't collide with sibling install-script test classes running in
    // parallel (shared canonical path = cross-class race) and doesn't litter the
    // runner's real C:\ProgramData. xUnit news-up this class per test method.
    private readonly string _programData =
        Path.Combine(Path.GetTempPath(), $"squid-install-pd-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_programData)) Directory.Delete(_programData, recursive: true); }
        catch { /* best-effort */ }
    }

    // ========================================================================
    // A1.h + A4.h + A7.h — Happy path: custom dir + DOWNLOAD_BASE override
    //                       + -NoServiceInstall extracts binary cleanly
    // ========================================================================

    [Fact]
    public async Task InstallScript_HappyPath_WithCustomDirAndMirror_ExtractsBinary()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mirror = LocalReleaseMirror.Start();
        using var ctx = new InstallScriptTestContext();

        // Stage a tiny fake binary the script will extract + verify exists.
        // Production verifies via `Test-Path $binaryPath` (line 219 of
        // install-tentacle.ps1) — the binary doesn't need to actually run.
        mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes("# fake test binary\n"));

        var (exitCode, stdout, stderr) = await RunInstallScriptAsync(
            "-Version", "1.6.0-test",
            "-InstallDir", ctx.InstallDir,
            "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
            "-NoServiceInstall"
        );

        exitCode.ShouldBe(0,
            customMessage: $"install-tentacle.ps1 happy path MUST exit 0. stdout:\n{stdout}\nstderr:\n{stderr}");

        // Binary extracted to the requested install dir.
        var expectedBinaryPath = Path.Combine(ctx.InstallDir, "Squid.Tentacle.exe");
        File.Exists(expectedBinaryPath).ShouldBeTrue(
            customMessage: $"Squid.Tentacle.exe MUST exist at {expectedBinaryPath} after install-tentacle.ps1 -NoServiceInstall completes. stdout:\n{stdout}");

        // Mirror was actually hit (proves DOWNLOAD_BASE override took effect,
        // not silently fell back to github.com).
        mirror.ReceivedRequests.ShouldNotBeEmpty(
            customMessage: "DOWNLOAD_BASE override MUST cause script to hit the local mirror. If empty, script fell through to github.com.");
        mirror.ReceivedRequests[0].ShouldContain("squid-tentacle",
            customMessage: $"download path MUST include 'squid-tentacle' filename. Got: {mirror.ReceivedRequests[0]}");
        mirror.ReceivedRequests[0].ShouldContain("1.6.0-test",
            customMessage: $"download path MUST include the requested version. Got: {mirror.ReceivedRequests[0]}");
    }

    // ========================================================================
    // A1.u1 + A2.u1 — Bogus version: mirror returns 404 → script exits non-zero
    // ========================================================================

    [Fact]
    public async Task InstallScript_BogusVersion_ScriptExitsNonZero()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mirror = LocalReleaseMirror.Start();
        using var ctx = new InstallScriptTestContext();

        // Mirror returns 404 for both the un-prefixed AND v-prefixed tag
        // — exhausts the script's fallback URL list.
        mirror.ConfigureNotFoundForVersion("99.99.99-bogus");

        var (exitCode, stdout, stderr) = await RunInstallScriptAsync(
            "-Version", "99.99.99-bogus",
            "-InstallDir", ctx.InstallDir,
            "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
            "-NoServiceInstall"
        );

        exitCode.ShouldNotBe(0,
            customMessage: $"install-tentacle.ps1 MUST exit non-zero when all download URLs 404. stdout:\n{stdout}\nstderr:\n{stderr}");

        // No binary extracted (script bailed before extract).
        var binaryPath = Path.Combine(ctx.InstallDir, "Squid.Tentacle.exe");
        File.Exists(binaryPath).ShouldBeFalse(
            customMessage: $"binary MUST NOT exist at {binaryPath} when all download URLs 404 — script must fail BEFORE extract.");

        // Script tried multiple URLs (un-prefixed + v-prefixed fallback).
        mirror.ReceivedRequests.Count.ShouldBeGreaterThanOrEqualTo(1,
            customMessage: $"script MUST attempt at least one download. ReceivedRequests: [{string.Join(", ", mirror.ReceivedRequests)}]");
    }

    // ========================================================================
    // A8.h — Re-running over existing install succeeds (idempotent)
    // ========================================================================

    [Fact]
    public async Task InstallScript_ReRun_OverExistingInstall_Succeeds()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mirror = LocalReleaseMirror.Start();
        using var ctx = new InstallScriptTestContext();

        mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes("# v1\n"));

        // First install.
        var firstResult = await RunInstallScriptAsync(
            "-Version", "1.6.0-test",
            "-InstallDir", ctx.InstallDir,
            "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
            "-NoServiceInstall"
        );

        firstResult.exitCode.ShouldBe(0,
            customMessage: $"first install MUST succeed. stdout:\n{firstResult.stdout}\nstderr:\n{firstResult.stderr}");

        // Second install over the same dir overwrites. The fake text binary can't
        // report a version, so the script takes the flat-layout fallback whose
        // Copy-Item -Force overwrites the install dir (extraction itself goes into
        // a fresh per-run staging dir, so ZipFile.ExtractToDirectory never collides).
        // Stage a different content so we can verify the second install took.
        mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes("# v2\n"));

        var secondResult = await RunInstallScriptAsync(
            "-Version", "1.6.0-test",
            "-InstallDir", ctx.InstallDir,
            "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
            "-NoServiceInstall"
        );

        secondResult.exitCode.ShouldBe(0,
            customMessage: $"second install over existing dir MUST succeed (idempotent). stdout:\n{secondResult.stdout}\nstderr:\n{secondResult.stderr}");

        // Binary content was updated (proves the re-install overwrote the install dir).
        var binaryPath = Path.Combine(ctx.InstallDir, "Squid.Tentacle.exe");
        var contentAfterSecond = await File.ReadAllTextAsync(binaryPath);
        contentAfterSecond.ShouldContain("v2",
            customMessage: $"after re-install with new content, binary MUST reflect the new version. Got: '{contentAfterSecond}'. Copy-Item -Force (flat fallback) isn't overwriting the install dir?");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>powershell.exe -NoProfile -NonInteractive -ExecutionPolicy
    /// Bypass -File install-tentacle.ps1 &lt;args&gt;</c>. Returns exit code +
    /// captured stdout + stderr. 60s timeout is generous — the install
    /// script's whole job (download + extract + Test-Path) should complete
    /// in under 5s against a loopback mirror; if it hangs, that's a
    /// script-level regression worth surfacing.
    /// </summary>
    private async Task<(int exitCode, string stdout, string stderr)> RunInstallScriptAsync(params string[] scriptArgs)
    {
        var scriptPath = LocateInstallScript();

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Redirect the child's %ProgramData% so install-info.json lands in this
        // test's isolated dir, never the shared canonical path (cross-class race).
        psi.EnvironmentVariables["ProgramData"] = _programData;

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        foreach (var arg in scriptArgs) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch powershell.exe");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("install-tentacle.ps1 did not exit within 60s");
        }

        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Resolves <c>deploy/scripts/install-tentacle.ps1</c> via the test
    /// assembly's directory (walk up to repo root, then descend).
    /// </summary>
    private static string LocateInstallScript()
    {
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var dir = thisAssemblyDir;

        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "deploy", "scripts", "install-tentacle.ps1");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException("Could not locate deploy/scripts/install-tentacle.ps1 from the test assembly's directory tree");
    }

    /// <summary>
    /// Test-scope context: GUID-unique install dir under %TEMP%, IDisposable
    /// cleanup. Using a user-owned path (not Program Files) skips the
    /// elevation check in install-tentacle.ps1 (line 122).
    /// </summary>
    private sealed class InstallScriptTestContext : IDisposable
    {
        public string InstallDir { get; }

        public InstallScriptTestContext()
        {
            InstallDir = Path.Combine(Path.GetTempPath(), $"squid-install-e2e-{Guid.NewGuid():N}");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(InstallDir))
                    Directory.Delete(InstallDir, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}
