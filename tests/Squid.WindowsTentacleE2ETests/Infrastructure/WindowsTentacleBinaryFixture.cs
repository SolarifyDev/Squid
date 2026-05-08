using System.Diagnostics;
using System.Reflection;

namespace Squid.WindowsTentacleE2ETests.Infrastructure;

/// <summary>
/// Phase 13 PR-2 — Windows mirror of
/// <c>Squid.LinuxTentacleE2ETests.Infrastructure.LinuxTentacleBinaryFixture</c>.
/// Builds the REAL <c>Squid.Tentacle.exe</c> binary (self-contained
/// win-x64) once per test process and shares the path across all
/// tests that need to drive its CLI surface.
///
/// <para><b>Why a real binary, not a placeholder</b>: the existing
/// Windows test suite calls into production command classes
/// (<c>RegisterCommand.ExecuteAsync</c>, <c>ServiceCommand.ExecuteAsync</c>)
/// directly via the public seam — Rule 12.9. That's the right pattern
/// for testing CLI argv parsing + per-command logic. But Phase 13 is
/// after a different kind of confidence: a real <c>dotnet publish</c>'d
/// single-file exe that systemd/SCM can launch at the binary path
/// stamped into the unit / SCM entry. Calling the production class
/// in-process can't catch publish-pipeline regressions (PublishSingleFile
/// target drift, RID mismatch, missing native dependency in self-
/// contained bundle).</para>
///
/// <para><b>UNBLOCKS Phase 13 PR-3</b>: real-binary-as-polling-agent
/// E2E test (mirror of Linux <c>R1h_RealBinary_PollingAgent_*</c>).
/// PR-3's test consumes this fixture to run the actual exe.</para>
///
/// <para><b>Build strategy</b>: <c>dotnet publish</c> with the same flags
/// production CI uses (<c>-r win-x64 --self-contained true
/// -p:PublishSingleFile=true</c>) into a stable cache path under
/// <c>bin/</c>. First call to <see cref="EnsureBuilt"/> takes ~30-60s
/// (cold .NET SDK build); subsequent calls return immediately from
/// the cached binary path.</para>
///
/// <para><b>Process-wide singleton</b>: build under a <see cref="object"/>
/// lock so concurrent test classes that share the binary don't race on
/// the dotnet publish invocation. xUnit's
/// <c>WindowsTentacleHostStateCollection</c> serializes the consumers,
/// but the collection's first test is what triggers the build — if
/// classes ever escape the collection, the lock prevents double-build.</para>
///
/// <para><b>Skip-on-non-Windows</b>: the binary is built for win-x64
/// self-contained → won't run on macOS / Linux. <see cref="IsAvailable"/>
/// returns true only on Windows + when <c>dotnet</c> is on PATH.</para>
/// </summary>
public sealed class WindowsTentacleBinaryFixture
{
    private static readonly object _buildLock = new();
    private static string _cachedBinaryPath;
    private static string _cachedV2BinaryPath;

    /// <summary>
    /// Pinned version stamp baked into the binary at publish time via
    /// <c>-p:Version=...</c>. The <c>version</c> subcommand reads
    /// <c>AssemblyVersion.Canonical</c> which is computed from the
    /// binary's NUMERIC <c>AssemblyName.Version</c> (4-part
    /// <c>Major.Minor.Build.Revision</c>), with trailing <c>.0</c>
    /// stripped — pre-release suffixes from
    /// <c>AssemblyInformationalVersion</c> are NOT included.
    ///
    /// <para>Therefore <see cref="BuildVersion"/> must be a 3-component
    /// numeric version. <c>99.99.99</c> chosen so it's:
    /// <list type="bullet">
    ///   <item>Distinguishable from any real production version
    ///         (currently 1.x — semver guard against accidental
    ///         match if test fixture leaks into production paths).</item>
    ///   <item>Survives <c>AssemblyVersion.Canonical</c>'s computation
    ///         unchanged: published Version="99.99.99" → numeric
    ///         "99.99.99.0" → strip ".0" → "99.99.99".</item>
    /// </list></para>
    ///
    /// <para>Mirrors the Linux fixture's <c>BuildVersion</c> for cross-
    /// platform consistency — a unit test asserting "99.99.99" output
    /// from <c>version</c> works on either OS.</para>
    /// </summary>
    public const string BuildVersion = "99.99.99";

    /// <summary>
    /// Pinned version stamp for the v2 (upgrade-target) binary build.
    /// Same numeric-only constraint as <see cref="BuildVersion"/>.
    /// <c>100.0.0</c> chosen so it's:
    /// <list type="bullet">
    ///   <item>STRICTLY GREATER than <see cref="BuildVersion"/> (semver
    ///         well-ordering: an upgrade test that asserts "version went
    ///         up" reads naturally).</item>
    ///   <item>Distinguishable from any real production version (currently
    ///         1.x).</item>
    /// </list>
    /// Mirrors Linux fixture's <c>BuildVersionV2</c>. Reserved for future
    /// Phase 13 upgrade-binary-integration tests on Windows.
    /// </summary>
    public const string BuildVersionV2 = "100.0.0";

    public static bool IsAvailable => OperatingSystem.IsWindows() && IsDotnetOnPath();

    /// <summary>
    /// Builds the binary if not already cached. Returns the absolute path
    /// to the executable (with <c>.exe</c> suffix on Windows). Idempotent
    /// + thread-safe.
    /// </summary>
    public string EnsureBuilt()
    {
        lock (_buildLock)
        {
            if (_cachedBinaryPath != null && File.Exists(_cachedBinaryPath))
                return _cachedBinaryPath;

            var binaryPath = Build(BuildVersion, "win-x64");

            if (!File.Exists(binaryPath))
                throw new InvalidOperationException(
                    $"dotnet publish completed but binary not found at expected path: {binaryPath}. " +
                    "Likely cause: production csproj's OutputType / publish target changed (Squid.Tentacle no longer produces a single-file Exe). " +
                    "Confirm via `dotnet publish ... -o <out>` and inspect the directory.");

            _cachedBinaryPath = binaryPath;
            return _cachedBinaryPath;
        }
    }

    /// <summary>
    /// Builds (or returns the cached path of) the v2 binary stamped at
    /// <see cref="BuildVersionV2"/>. Reserved for Phase 13 upgrade tests
    /// that need both v1 + v2 simultaneously.
    /// </summary>
    public string EnsureBuiltV2()
    {
        lock (_buildLock)
        {
            if (_cachedV2BinaryPath != null && File.Exists(_cachedV2BinaryPath))
                return _cachedV2BinaryPath;

            var binaryPath = Build(BuildVersionV2, "win-x64-v2");

            if (!File.Exists(binaryPath))
                throw new InvalidOperationException(
                    $"v2 dotnet publish completed but binary not found at: {binaryPath}.");

            _cachedV2BinaryPath = binaryPath;
            return _cachedV2BinaryPath;
        }
    }

    /// <summary>
    /// Runs the binary with the given args + a clean environment.
    /// Returns (exitCode, combined stdout+stderr). 60s wall-clock cap —
    /// individual subcommands like <c>version</c> are sub-second; longer
    /// timeouts indicate hangs that should fail-fast.
    ///
    /// <para><b>Note on elevation</b>: unlike Linux's
    /// <c>SudoRun</c>/<c>Run</c> distinction (where sudo is required for
    /// <c>service install/uninstall</c>, <c>register</c> writing under
    /// <c>/etc/squid-tentacle/</c>, etc.), Windows tests assume the test
    /// runner process is already running as Administrator (the
    /// <c>windows-latest</c> GHA runner satisfies this). All operations
    /// — including <c>service install</c> writing the SCM entry —
    /// inherit that elevation. No sudo equivalent needed.</para>
    /// </summary>
    public (int exitCode, string output) Run(params string[] args)
    {
        var binaryPath = EnsureBuilt();

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch {binaryPath}");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException($"{binaryPath} {string.Join(' ', args)} did not complete within 60s.");
        }

        return (proc.ExitCode, stdoutTask.Result + Environment.NewLine + stderrTask.Result);
    }

    /// <summary>
    /// Spawns the binary as a long-running detached process — caller is
    /// responsible for stopping it. Used by Phase 13 PR-3's
    /// real-binary-as-polling-agent test where the binary's <c>run</c>
    /// command needs to stay alive while the stub server dispatches a
    /// script through the polling channel.
    ///
    /// <para>Returns the underlying <see cref="Process"/> so the caller
    /// can: (a) <c>Kill</c> it on cleanup, (b) read stdout/stderr for
    /// diagnostics on failure paths.</para>
    ///
    /// <para><b>Why a separate method</b>: <see cref="Run"/> blocks until
    /// the process exits and returns combined output. That's right for
    /// short subcommands (<c>version</c>, <c>register</c>) but wrong for
    /// long-running (<c>run</c>) — we need the process running while we
    /// drive the stub server. Caller is responsible for cleanup.</para>
    /// </summary>
    public Process StartLongRunning(params string[] args)
    {
        var binaryPath = EnsureBuilt();

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        return Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch {binaryPath} {string.Join(' ', args)}");
    }

    private static string Build(string versionStamp, string outputSubDir)
    {
        var repoRoot = LocateRepoRoot();
        var csproj = Path.Combine(repoRoot, "src", "Squid.Tentacle", "Squid.Tentacle.csproj");

        if (!File.Exists(csproj))
            throw new FileNotFoundException(
                $"Could not locate Squid.Tentacle.csproj at {csproj}. Repo root resolved to {repoRoot}.");

        // Stable cache path under the test assembly's bin/. Survives between
        // test runs but cleared by `dotnet clean` / `git clean -xdf bin/`.
        // The outputSubDir parameter lets v1 + v2 coexist in separate cache
        // dirs (e.g. win-x64/ and win-x64-v2/).
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var outputDir = Path.Combine(thisAssemblyDir, "squid-tentacle-binary-fixture", outputSubDir);

        Directory.CreateDirectory(outputDir);

        // Mirror production CI's publish flags exactly — single self-
        // contained binary, stamped version. Same flags as the Linux
        // fixture except RID = win-x64.
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add(csproj);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add("win-x64");
        psi.ArgumentList.Add("--self-contained");
        psi.ArgumentList.Add("true");
        psi.ArgumentList.Add("-p:PublishSingleFile=true");
        psi.ArgumentList.Add($"-p:Version={versionStamp}");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputDir);
        // Quiet verbosity — full build log isn't useful for test diagnosis;
        // errors still surface on stderr.
        psi.ArgumentList.Add("--verbosity");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("--nologo");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet publish");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        // Cold .NET SDK + restore + publish + self-contained runtime
        // bundle ≈ 30-60s; budget 5min for the worst case (CI cold cache,
        // fresh NuGet downloads).
        if (!proc.WaitForExit(300_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new TimeoutException(
                "dotnet publish did not complete within 5min. Cold cache + NuGet restore typically takes 1-2min; longer indicates a build hang OR a dependency network issue.");
        }

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet publish failed with exit {proc.ExitCode}.\n" +
                $"stdout: {stdoutTask.Result}\n" +
                $"stderr: {stderrTask.Result}");
        }

        return Path.Combine(outputDir, "Squid.Tentacle.exe");
    }

    private static string LocateRepoRoot()
    {
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var dir = thisAssemblyDir;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            // Repo root marker: presence of .github/ + src/Squid.Tentacle/
            var githubDir = Path.Combine(dir, ".github");
            var tentacleDir = Path.Combine(dir, "src", "Squid.Tentacle");
            if (Directory.Exists(githubDir) && Directory.Exists(tentacleDir))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException(
            $"Could not locate repo root (no .github/ + src/Squid.Tentacle/ ancestor of {thisAssemblyDir}).");
    }

    private static bool IsDotnetOnPath()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--version");

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
