using System.Diagnostics;

namespace Squid.WindowsUpgradeE2ETests.Infrastructure;

/// <summary>
/// reusable Windows service install/start/stop/uninstall
/// fixture used by the upgrade-pipeline E2E tests. Wraps <c>sc.exe</c>
/// (mirroring 's <c>WindowsServiceHost</c> production code path
/// exactly — the production SCM choice + this test fixture stay in sync) with:
///
/// <list type="bullet">
///   <item>Idempotent install: pre-existing service with the same name is
///         deleted first so a previous failed test doesn't block the next.</item>
///   <item>Start with timeout: waits up to 30s for the service to reach
///         the Running state. SCM accepts the start command async; we poll
///         <c>sc query</c> until <c>STATE</c> is <c>RUNNING</c> or timeout.</item>
///   <item>Marker-file polling: independent verification that the test service
///         actually wrote its <c>service-running.marker</c> file from
///         OnStart — proves the start signal made it through SCM, the
///         service process actually ran, AND the version-file IO logic
///         executed. Three pieces of evidence in one assertion.</item>
///   <item>Best-effort cleanup via <see cref="IDisposable"/>: even if a
///         test fails partway, Stop + Delete fire so the SCM database
///         doesn't accumulate orphan services.</item>
/// </list>
///
/// <para><b>Generic for future use</b>: nothing in this fixture is upgrade-
/// specific. Any test needing a controllable Windows service can use the
/// fixture by passing a different <c>ServiceName</c> + <c>InstallDir</c>.
/// Future Chocolatey/MSI method tests will reuse it.</para>
///
/// <para><b>Skip-on-non-Windows</b>: the fixture's <see cref="IsAvailable"/>
/// flag is the canonical guard. Tests check this and no-op-skip on
/// macOS/Linux dev hosts (mirrors the established pattern in the
/// existing <c>WindowsUpgradeWrapperE2ETests</c>).</para>
/// </summary>
public sealed class WindowsServiceFixture : IDisposable
{
    private readonly string _serviceName;
    private readonly string _installDir;
    private bool _installed;

    /// <summary>
    /// True only on Windows. Tests should guard with this — every step
    /// (install/start/stop/uninstall) requires sc.exe + admin context that
    /// only exists on Windows + the GHA <c>windows-latest</c> runner.
    /// </summary>
    public static bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>The service name registered with sc.exe. Used for sc query / start / stop / delete.</summary>
    public string ServiceName => _serviceName;

    /// <summary>The directory containing the service exe + version.txt + marker. Used to drive the version-swap pattern in upgrade tests.</summary>
    public string InstallDir => _installDir;

    /// <summary>The full path to the service exe inside <see cref="InstallDir"/>.</summary>
    public string ServiceExePath => Path.Combine(_installDir, "SquidUpgradeE2ETestService.exe");

    /// <summary>The path to the version.txt file the service reads on Start.</summary>
    public string VersionFilePath => Path.Combine(_installDir, "version.txt");

    /// <summary>The path to the marker file the service writes on Start (containing the version it read).</summary>
    public string MarkerFilePath => Path.Combine(_installDir, "service-running.marker");

    /// <summary>
    /// Construct the fixture against a UNIQUE service name + UNIQUE install
    /// dir. Caller responsibility to make these unique across concurrent
    /// fixtures (recommended: GUID-suffix the name to avoid sc-database
    /// collisions if tests fail to clean up).
    /// </summary>
    public WindowsServiceFixture(string serviceName, string installDir)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentException("serviceName is required", nameof(serviceName));
        if (string.IsNullOrWhiteSpace(installDir))
            throw new ArgumentException("installDir is required", nameof(installDir));

        _serviceName = serviceName;
        _installDir = installDir;
    }

    /// <summary>
    /// Stage the test service exe at <see cref="InstallDir"/> with the
    /// supplied initial version, then sc.exe-create + sc.exe-start the
    /// service, polling until it reaches the RUNNING state OR the timeout
    /// expires.
    ///
    /// <para><b>Idempotent</b>: if a previous test left the service
    /// installed, it's deleted first. Best-effort.</para>
    ///
    /// <para><b>Returns</b>: nothing (throws on failure). Polling proof of
    /// "service is actually running and has read version V" is via the
    /// marker file at <see cref="MarkerFilePath"/> — caller asserts
    /// content after this method returns.</para>
    /// </summary>
    public void InstallAndStart(string testServiceExeSourcePath, string initialVersion, TimeSpan startTimeout)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("WindowsServiceFixture only runs on Windows");

        // Cleanup any stale prior install of the SAME service name. Best-effort
        // — rc=1060 (service does not exist) is the common case and not an error.
        TryDeleteService(_serviceName);

        Directory.CreateDirectory(_installDir);

        // Copy the ENTIRE source directory (not just the .exe). A framework-
        // dependent .NET 9 service exe is just an apphost shim; it needs its
        // sibling files (.dll, .runtimeconfig.json, .deps.json, dependent
        // assemblies like System.ServiceProcess.ServiceController.dll) in the
        // SAME directory to resolve the managed entry point. SCM-spawned
        // processes get a minimal env, so PATH/probing tricks don't help —
        // the only reliable path is "co-locate everything".
        // Without this, apphost launches → tries to load the managed DLL →
        // can't find runtimeconfig.json → exits silently before calling
        // ServiceBase.Run → SCM times out → "[SC] StartService FAILED 1053:
        // The service did not respond to the start or control request in a
        // timely fashion." Caught the first time the fixture ran on a real
        // windows-latest GHA runner.
        var sourceDir = Path.GetDirectoryName(testServiceExeSourcePath)
            ?? throw new InvalidOperationException($"Test service source path has no directory: {testServiceExeSourcePath}");
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
            var destFile = Path.Combine(_installDir, relativePath);
            var destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            File.Copy(sourceFile, destFile, overwrite: true);
        }

        // Test service reads version.txt on Start; pre-populate it with the
        // caller's initial version so the marker file content is predictable.
        File.WriteAllText(VersionFilePath, initialVersion);

        // sc.exe create — same shape as production WindowsServiceHost.
        // CRITICAL: each `key=` and its value MUST be SEPARATE argv tokens
        // (NOT packed into one `"key= value"` string). When .NET packs
        // `"binPath= \"...\" --service"` as a single ArgumentList element,
        // it gets quoted-as-one-token → sc.exe consumes the whole quoted
        // chunk as the binPath value and bails with "Invalid type= field"
        // on the NEXT option. The split-token form below matches what
        // CommandLineToArgvW produces from the documented command-line
        // example `sc.exe create NewService binPath= "..." type= own`.
        RunScExpectingSuccess(
            "/Create failed",
            "create", _serviceName,
            "binPath=", $"\"{ServiceExePath}\" --service",
            "type=", "own",
            "start=", "demand"   // demand-start — test controls start/stop explicitly
        );
        _installed = true;

        // sc.exe start — async; SCM accepts and the service process spawns.
        RunScExpectingSuccess(
            "/Start failed",
            "start", _serviceName
        );

        WaitForState("RUNNING", startTimeout);
    }

    /// <summary>
    /// sc.exe-stop the service, polling until it reaches STOPPED state.
    /// </summary>
    public void Stop(TimeSpan stopTimeout)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("WindowsServiceFixture only runs on Windows");
        if (!_installed) return;

        try
        {
            RunSc("stop", _serviceName);   // ignore rc; service may already be stopped
            WaitForState("STOPPED", stopTimeout);
        }
        catch
        {
            // Best-effort. Caller's main assertions don't depend on the
            // stop path; the cleanup in Dispose() will force-delete anyway.
        }
    }

    /// <summary>
    /// sc.exe-delete the service. Idempotent — missing service returns
    /// non-zero but we don't propagate.
    /// </summary>
    public void Uninstall()
    {
        if (!IsAvailable) return;

        TryDeleteService(_serviceName);
        _installed = false;
    }

    /// <summary>
    /// Best-effort cleanup. Stop + Delete + remove install dir. Even if a
    /// test fails partway, this MUST drop the service from SCM database
    /// and clean the temp dir so the next test run starts cold.
    /// </summary>
    public void Dispose()
    {
        if (!IsAvailable) return;

        try { Stop(TimeSpan.FromSeconds(10)); } catch { /* best-effort */ }
        try { Uninstall(); } catch { /* best-effort */ }

        try
        {
            if (Directory.Exists(_installDir))
                Directory.Delete(_installDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ── sc.exe orchestration ─────────────────────────────────────────────────

    private void TryDeleteService(string serviceName)
    {
        // First attempt to stop (in case a prior test left it running),
        // then delete. Both are idempotent — any non-zero exit is just
        // "wasn't running" or "wasn't installed", which is exactly the
        // state we want to end up in.
        try { RunSc("stop", serviceName); } catch { }
        try { RunSc("delete", serviceName); } catch { }
    }

    private void RunScExpectingSuccess(string failureContext, params string[] args)
    {
        var (exitCode, stdout, stderr) = RunSc(args);
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"sc.exe {failureContext} (exit {exitCode}). args=[{string.Join(" ", args)}]\n" +
                $"stdout: {stdout}\nstderr: {stderr}");
    }

    private static (int exitCode, string stdout, string stderr) RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch sc.exe");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit(15_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("sc.exe did not exit within 15s");
        }

        return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// Polls <c>sc query &lt;name&gt;</c> stdout for the literal "STATE: ... <c>expected</c>"
    /// substring. SCM transitions are async; sc start returns immediately
    /// after acceptance, so we need explicit polling. 200ms cadence keeps
    /// CPU low; 30s default cap covers slow GHA runners under load.
    /// </summary>
    private void WaitForState(string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var (exitCode, stdout, _) = RunSc("query", _serviceName);
            if (exitCode == 0 && stdout.Contains(expected, StringComparison.OrdinalIgnoreCase))
                return;
            Thread.Sleep(200);
        }
        throw new TimeoutException(
            $"Service '{_serviceName}' did not reach STATE={expected} within {timeout}. " +
            "Check sc query output via running sc.exe query manually on the host.");
    }
}
