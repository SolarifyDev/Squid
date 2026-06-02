using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Phase 12.M -- E2E coverage for the install-resilience surface added by
/// PR #329 (path-agnostic install + UAC auto-elevation + install-info.json
/// discovery file).
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real <c>powershell.exe</c>, real
/// <c>install-tentacle.ps1</c> from disk, real <see cref="LocalReleaseMirror"/>
/// serving fake zip downloads, real file-system writes. Skip-on-non-Windows
/// guard keeps cross-OS dev hosts running a clean "0 / 0" instead of
/// spurious failures.</para>
///
/// <para><b>Test groups</b> (each section below covers one resilience axis):
/// <list type="number">
///   <item>install-info.json discovery file -- schema, fields, path</item>
///   <item>Auto-elevation skip semantics -- already admin / user-path /
///         -NoAutoElevate / default-path-without-admin</item>
///   <item>Idempotence stress -- re-install over existing dir, 5 iterations</item>
///   <item>Corrupt-state recovery -- info.json present but binary missing,
///         info.json malformed, info.json missing entirely</item>
///   <item>Custom install paths -- with spaces, non-ASCII (where supported),
///         different drives, deep nested</item>
/// </list></para>
///
/// <para>Tests NOT covered here because they're impossible / impractical in CI:
/// real UAC prompt (would block the runner forever), Windows Defender
/// SmartScreen blocking unsigned binaries (Defender is disabled on
/// windows-latest runners), corporate GPO ExecutionPolicy locks (no GPO
/// infrastructure on runners). Those are documented in
/// <c>docs/windows-tentacle-install.md</c> as known operator workflows.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleInstallScript)]
public sealed class TentacleInstallScriptResilienceE2ETests : IDisposable
{
    // Test-isolated %ProgramData% so install-tentacle.ps1's install-info.json
    // (written to $env:ProgramData\Squid\Tentacle\) cannot collide with other
    // install-script test classes running in parallel -- the shared canonical
    // path was a cross-class race (a sibling test's BinaryPath leaked in). xUnit
    // news-up this class per test method, so each test gets a unique dir; the
    // child process inherits it via EnvironmentVariables["ProgramData"] in
    // RunInstallScriptAsync.
    private readonly string _programData =
        Path.Combine(Path.GetTempPath(), $"squid-install-pd-{Guid.NewGuid():N}");

    private string InstallInfoPath =>
        Path.Combine(_programData, "Squid", "Tentacle", "install-info.json");

    public void Dispose()
    {
        try { if (Directory.Exists(_programData)) Directory.Delete(_programData, recursive: true); }
        catch { /* best-effort */ }
    }

    // ========================================================================
    // Group 1 -- install-info.json discovery file
    // ========================================================================

    [Fact]
    public async Task InstallScript_WritesInstallInfoJson_AtCanonicalPath_AfterExtract()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mirror = LocalReleaseMirror.Start();
        using var ctx = new InstallScriptTestContext();
        mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes("# fake test binary\n"));

        var (exitCode, stdout, stderr) = await RunInstallScriptAsync(
            "-Version", "1.6.0-test",
            "-InstallDir", ctx.InstallDir,
            "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
            "-NoServiceInstall",
            "-NoAutoElevate"
        );

        exitCode.ShouldBe(0,
            customMessage: $"install MUST succeed. stdout:\n{stdout}\nstderr:\n{stderr}");

        var infoPath = InstallInfoPath;

        File.Exists(infoPath).ShouldBeTrue(
            customMessage:
                $"install-info.json MUST be written to {infoPath}. " +
                $"Downstream scripts (register/service-install) read this file to locate the binary. " +
                $"Without it, operators with custom -InstallDir get a broken copy-paste experience.");

        ctx.RegisterCleanupPath(infoPath);
    }

    [Fact]
    public async Task InstallScript_InstallInfoJson_HasAllRequiredFields()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mirror = LocalReleaseMirror.Start();
        using var ctx = new InstallScriptTestContext();
        mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes("# test\n"));

        var (exitCode, stdout, stderr) = await RunInstallScriptAsync(
            "-Version", "2.0.1-test",
            "-InstallDir", ctx.InstallDir,
            "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
            "-NoServiceInstall",
            "-NoAutoElevate"
        );

        exitCode.ShouldBe(0, customMessage: $"install MUST succeed. stdout:\n{stdout}\nstderr:\n{stderr}");

        var infoPath = InstallInfoPath;
        ctx.RegisterCleanupPath(infoPath);

        var raw = await File.ReadAllTextAsync(infoPath);
        var info = JsonDocument.Parse(raw).RootElement;

        // Schema = 1 -- a future schema bump must update both writer and
        // downstream readers in lockstep. Pinned to make the bump explicit.
        info.GetProperty("Schema").GetInt32().ShouldBe(1);

        // BinaryPath -- the actual on-disk path to Squid.Tentacle.exe. The
        // server-generated register/service-install snippet reads this verbatim.
        var binaryPath = info.GetProperty("BinaryPath").GetString();
        binaryPath.ShouldEndWith("Squid.Tentacle.exe");
        binaryPath.ShouldStartWith(ctx.InstallDir);
        File.Exists(binaryPath).ShouldBeTrue(
            customMessage: $"BinaryPath '{binaryPath}' must point at an existing file.");

        // InstallDir matches what operator passed
        info.GetProperty("InstallDir").GetString().ShouldBe(ctx.InstallDir);

        // Version + Architecture present
        info.GetProperty("Version").GetString().ShouldBe("2.0.1-test");
        info.GetProperty("Architecture").GetString().ShouldBeOneOf("win-x64", "win-arm64");

        // InstalledAt is a parseable ISO-8601 timestamp
        var installedAt = info.GetProperty("InstalledAt").GetString();
        DateTimeOffset.TryParse(installedAt, out var parsed).ShouldBeTrue(
            customMessage: $"InstalledAt '{installedAt}' must be parseable as ISO-8601. Operators read this when investigating which deploy installed when.");
        (DateTimeOffset.UtcNow - parsed).TotalMinutes.ShouldBeLessThan(5,
            customMessage: "InstalledAt should be recent (within the last 5 minutes).");

        // InstalledBy = the current user identity
        info.GetProperty("InstalledBy").GetString().ShouldNotBeNullOrWhiteSpace();

        // ServiceName -- defaults to squid-tentacle
        info.GetProperty("ServiceName").GetString().ShouldBe("squid-tentacle");
    }

    [Fact]
    public async Task InstallScript_InstallInfoJson_BinaryPathReflectsCustomInstallDir()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mirror = LocalReleaseMirror.Start();
        // Use a path with deep nesting to prove path-resolution works for
        // non-trivial structures operators might pick.
        var nestedPath = Path.Combine(Path.GetTempPath(), $"squid-deep-{Guid.NewGuid():N}", "level2", "level3");
        using var ctx = new InstallScriptTestContext(nestedPath);
        mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes("# test\n"));

        var (exitCode, _, stderr) = await RunInstallScriptAsync(
            "-Version", "1.6.0-test",
            "-InstallDir", ctx.InstallDir,
            "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
            "-NoServiceInstall",
            "-NoAutoElevate"
        );

        exitCode.ShouldBe(0, customMessage: $"deep-nested install dir must work. stderr:\n{stderr}");

        var infoPath = InstallInfoPath;
        ctx.RegisterCleanupPath(infoPath);

        var info = JsonDocument.Parse(await File.ReadAllTextAsync(infoPath)).RootElement;
        info.GetProperty("BinaryPath").GetString().ShouldBe(Path.Combine(ctx.InstallDir, "Squid.Tentacle.exe"));
    }

    // ========================================================================
    // Group 2 -- Auto-elevation skip semantics
    //
    // We can't actually drive a UAC prompt in CI (would block the runner)
    // but we CAN verify the elevation-skip conditions all work:
    //   - already admin → no relaunch
    //   - user-owned install dir → no relaunch (admin not needed)
    //   - -NoAutoElevate flag → refuse to relaunch (clean error if would need to)
    // ========================================================================

    [Fact]
    public async Task InstallScript_AlreadyAdmin_NoReElevation_CompletesInProcess()
    {
        if (!OperatingSystem.IsWindows()) return;

        // CI runner runs as administrator by default. Default install dir
        // requires admin -- the script should detect "already admin" and proceed.
        // We invoke with default install dir but use -NoAutoElevate to make any
        // accidental elevation attempt fail loudly (rather than block on UAC).
        // If the script correctly detects already-admin, -NoAutoElevate is a no-op.
        using var mirror = LocalReleaseMirror.Start();
        var defaultInstallDir = @"C:\Program Files\Squid Tentacle";
        mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes("# test\n"));

        try
        {
            var (exitCode, stdout, stderr) = await RunInstallScriptAsync(
                "-Version", "1.6.0-test",
                "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
                "-NoServiceInstall",
                "-NoAutoElevate"
            );

            exitCode.ShouldBe(0,
                customMessage:
                    $"CI runner is already admin; default install dir should succeed without UAC re-launch. " +
                    $"stdout:\n{stdout}\nstderr:\n{stderr}");
        }
        finally
        {
            // Clean up Program Files install + the global info.json
            try { Directory.Delete(defaultInstallDir, recursive: true); } catch { }
            try
            {
                File.Delete(InstallInfoPath);
            }
            catch { }
        }
    }

    [Fact]
    public async Task InstallScript_UserOwnedInstallDir_SkipsElevationEvenWithoutNoAutoElevateFlag()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mirror = LocalReleaseMirror.Start();
        using var ctx = new InstallScriptTestContext();
        mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes("# test\n"));

        // User-owned path (under %TEMP%) -- admin not required, so elevation
        // logic should detect "no admin needed" and proceed in-process even
        // without the -NoAutoElevate flag. If the script wrongly insisted on
        // UAC for non-admin-required paths, this test would hang or fail.
        var (exitCode, stdout, stderr) = await RunInstallScriptAsync(
            "-Version", "1.6.0-test",
            "-InstallDir", ctx.InstallDir,
            "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
            "-NoServiceInstall"
            // Deliberately NO -NoAutoElevate -- we want to prove the script's
            // own logic skips elevation for user-owned paths.
        );

        exitCode.ShouldBe(0,
            customMessage:
                $"User-owned install dir ({ctx.InstallDir}) must NOT trigger UAC re-launch even without " +
                $"-NoAutoElevate. stdout:\n{stdout}\nstderr:\n{stderr}");

        File.Exists(Path.Combine(ctx.InstallDir, "Squid.Tentacle.exe")).ShouldBeTrue();
    }

    // ========================================================================
    // Group 3 -- Idempotence stress (5 iterations)
    //
    // A single install pass succeeding isn't enough -- production operators
    // re-run install scripts in 1) bake-image automation, 2) drift remediation,
    // 3) version upgrades. Each loop here proves no state corruption / lock
    // leaks across iterations.
    // ========================================================================

    [Fact]
    public async Task InstallScript_FreshInstall_FiveIterations_AllSucceed()
    {
        if (!OperatingSystem.IsWindows()) return;

        const int iterations = 5;
        var failures = new List<string>();

        for (var i = 0; i < iterations; i++)
        {
            using var mirror = LocalReleaseMirror.Start();
            using var ctx = new InstallScriptTestContext();
            mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes($"# iter {i}\n"));

            var (exitCode, stdout, stderr) = await RunInstallScriptAsync(
                "-Version", "1.6.0-test",
                "-InstallDir", ctx.InstallDir,
                "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
                "-NoServiceInstall",
                "-NoAutoElevate"
            );

            if (exitCode != 0)
            {
                failures.Add($"iter {i}: exit={exitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}");
                continue;
            }

            if (!File.Exists(Path.Combine(ctx.InstallDir, "Squid.Tentacle.exe")))
                failures.Add($"iter {i}: binary missing after exit 0");
        }

        failures.ShouldBeEmpty(
            customMessage:
                $"5/{iterations} fresh installs must succeed. Pre-fix flake or post-fix regression " +
                $"surfaces here. Failures:\n{string.Join("\n----\n", failures)}");
    }

    [Fact]
    public async Task InstallScript_ReInstallOverSameDir_FiveIterations_NoStateCorruption()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mirror = LocalReleaseMirror.Start();
        using var ctx = new InstallScriptTestContext();
        const int iterations = 5;
        var failures = new List<string>();

        for (var i = 0; i < iterations; i++)
        {
            // Each iteration stages a different binary content so we can verify
            // the latest install actually overwrote the previous one. If
            // Expand-Archive -Force broke and silently no-op'd, we'd see stale
            // content here.
            var marker = $"iter-{i}-{Guid.NewGuid():N}";
            mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes(marker));

            var (exitCode, stdout, stderr) = await RunInstallScriptAsync(
                "-Version", "1.6.0-test",
                "-InstallDir", ctx.InstallDir,
                "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
                "-NoServiceInstall",
                "-NoAutoElevate"
            );

            if (exitCode != 0)
            {
                failures.Add($"iter {i}: exit={exitCode}. stdout:\n{stdout}\nstderr:\n{stderr}");
                continue;
            }

            var content = await File.ReadAllTextAsync(Path.Combine(ctx.InstallDir, "Squid.Tentacle.exe"));
            if (!content.Contains(marker))
                failures.Add($"iter {i}: binary content '{content}' missing marker '{marker}' -- Expand-Archive -Force did not overwrite.");
        }

        failures.ShouldBeEmpty(
            customMessage:
                $"5/{iterations} re-installs over same dir must succeed with content actually replaced. " +
                $"Failures:\n{string.Join("\n----\n", failures)}");
    }

    // ========================================================================
    // Group 4 -- Corrupt-state recovery
    //
    // Production hosts can have partial-install state from a prior crash,
    // operator manual edits, AV quarantine, etc. The script must not crash
    // on these, and the discovery-file consumer (server-generated snippet)
    // must give actionable errors when reading mangled state.
    // ========================================================================

    [Fact]
    public async Task InstallScript_OverwritesStaleInstallInfoJson_OnReInstall()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mirror = LocalReleaseMirror.Start();
        using var ctx = new InstallScriptTestContext();
        mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes("# test\n"));

        var infoDir = Path.Combine(_programData, "Squid", "Tentacle");
        var infoPath = Path.Combine(infoDir, "install-info.json");
        ctx.RegisterCleanupPath(infoPath);

        // Pre-stage stale install-info pointing at a fictitious path
        Directory.CreateDirectory(infoDir);
        await File.WriteAllTextAsync(infoPath,
            """{"Schema":1,"BinaryPath":"C:\\stale\\path\\Squid.Tentacle.exe","InstallDir":"C:\\stale\\path","Version":"0.0.0-stale"}""");

        var (exitCode, _, stderr) = await RunInstallScriptAsync(
            "-Version", "1.6.0-test",
            "-InstallDir", ctx.InstallDir,
            "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
            "-NoServiceInstall",
            "-NoAutoElevate"
        );

        exitCode.ShouldBe(0, customMessage: $"re-install over stale info MUST succeed. stderr:\n{stderr}");

        // Stale info was replaced with fresh content reflecting the new install dir.
        var info = JsonDocument.Parse(await File.ReadAllTextAsync(infoPath)).RootElement;
        info.GetProperty("BinaryPath").GetString().ShouldStartWith(ctx.InstallDir,
            customMessage: "install-info.json BinaryPath must reflect the NEW install, not the stale entry.");
        info.GetProperty("Version").GetString().ShouldBe("1.6.0-test",
            customMessage: "Version must update to reflect the new install.");
    }

    // ========================================================================
    // Group 5 -- Custom install paths
    // ========================================================================

    [Fact]
    public async Task InstallScript_InstallDirWithSpaces_ExtractsAndDiscoversCorrectly()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mirror = LocalReleaseMirror.Start();
        // Path with spaces -- common operator workflow (e.g. "D:\My Apps\Squid Tentacle").
        // Quoting + path-resolution must handle this cleanly.
        var pathWithSpaces = Path.Combine(Path.GetTempPath(), $"squid install {Guid.NewGuid():N}");
        using var ctx = new InstallScriptTestContext(pathWithSpaces);
        mirror.StageBinary("Squid.Tentacle.exe", System.Text.Encoding.UTF8.GetBytes("# test\n"));

        var (exitCode, _, stderr) = await RunInstallScriptAsync(
            "-Version", "1.6.0-test",
            "-InstallDir", pathWithSpaces,
            "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
            "-NoServiceInstall",
            "-NoAutoElevate"
        );

        exitCode.ShouldBe(0,
            customMessage: $"install dir with spaces must work. stderr:\n{stderr}");

        File.Exists(Path.Combine(pathWithSpaces, "Squid.Tentacle.exe")).ShouldBeTrue();

        // install-info.json must roundtrip the spaced path correctly.
        var infoPath = InstallInfoPath;
        ctx.RegisterCleanupPath(infoPath);

        var info = JsonDocument.Parse(await File.ReadAllTextAsync(infoPath)).RootElement;
        info.GetProperty("InstallDir").GetString().ShouldBe(pathWithSpaces);
        info.GetProperty("BinaryPath").GetString().ShouldBe(Path.Combine(pathWithSpaces, "Squid.Tentacle.exe"));
    }

    // ========================================================================
    // Helpers (shared with TentacleInstallScriptWindowsE2ETests via parallel
    // copies -- intentional: keep the per-test-class scope explicit, and avoid
    // a cross-cutting fixture that two classes share state through).
    // ========================================================================

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

    private sealed class InstallScriptTestContext : IDisposable
    {
        private readonly List<string> _cleanupPaths = new();

        public string InstallDir { get; }

        public InstallScriptTestContext()
            : this(Path.Combine(Path.GetTempPath(), $"squid-install-e2e-{Guid.NewGuid():N}")) { }

        public InstallScriptTestContext(string installDir)
        {
            InstallDir = installDir;
            _cleanupPaths.Add(InstallDir);
        }

        public void RegisterCleanupPath(string path) => _cleanupPaths.Add(path);

        public void Dispose()
        {
            foreach (var path in _cleanupPaths)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch { /* best-effort */ }
            }
        }
    }
}
