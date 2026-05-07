using System.Diagnostics;
using System.Reflection;

namespace Squid.LinuxTentacleE2ETests.Infrastructure;

/// <summary>
/// Phase 12.M.L.B.0+ — fixture that builds the REAL <c>Squid.Tentacle</c>
/// binary (self-contained linux-x64) once per test process and shares
/// the path across all tests that need to drive its CLI surface.
///
/// <para>UNBLOCKS Section B (service-lifecycle CLI), Section C (register),
/// and Section G (multi-instance) — none of these can be tested with
/// the bash-script placeholder used for upgrade-flow E2E (the placeholder
/// has no <c>register</c> / <c>service install</c> handlers).</para>
///
/// <para><b>Why a real binary, not a placeholder</b>: the production
/// <c>squid-tentacle service install</c> command writes a systemd unit
/// that references the binary's actual path; <c>register</c> performs
/// real REST POST + cert validation; <c>create-instance</c> walks
/// real config dir layouts. Mocking these would slip to medium-fidelity
/// (Rule 12 tier 🟡) and miss the binding-layer regressions an E2E
/// is supposed to catch.</para>
///
/// <para><b>Build strategy</b>: <c>dotnet publish</c> with the same flags
/// production CI uses (<c>-r linux-x64 --self-contained true
/// -p:PublishSingleFile=true</c>) into a stable cache path under
/// <c>bin/</c>. First call to <see cref="EnsureBuilt"/> takes ~20-30s
/// (cold .NET SDK build); subsequent calls return immediately from
/// the cached binary path.</para>
///
/// <para><b>Process-wide singleton</b>: build under a <see cref="object"/>
/// lock so concurrent test classes that share the binary don't race
/// on the dotnet publish invocation. xUnit's
/// <c>LinuxTentacleHostStateCollection</c> serializes the consumers,
/// but the collection's first test is what triggers the build — if
/// classes ever escape the collection, the lock prevents double-build.</para>
///
/// <para><b>Skip-on-non-Linux</b>: the binary is built for linux-x64
/// self-contained → won't run on macOS / Windows. <see cref="IsAvailable"/>
/// returns true only on Linux + when <c>dotnet</c> is on PATH.</para>
/// </summary>
public sealed class LinuxTentacleBinaryFixture
{
    private static readonly object _buildLock = new();
    private static string _cachedBinaryPath;

    /// <summary>
    /// Pinned version stamp baked into the binary at publish time via
    /// <c>-p:Version=...</c>. The <c>version</c> subcommand reads
    /// <see cref="AssemblyVersion.Canonical"/> which is computed from
    /// the binary's NUMERIC <c>AssemblyName.Version</c> (4-part
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
    ///   <item>Survives <see cref="AssemblyVersion.Canonical"/>'s
    ///         computation unchanged: published Version="99.99.99" →
    ///         numeric "99.99.99.0" → strip ".0" → "99.99.99".</item>
    /// </list></para>
    ///
    /// <para>J.M.L.B.0 first runner caught my wrong assumption that
    /// <c>-p:Version=1.0.0-binarytest</c> would propagate the
    /// pre-release suffix to <c>version</c>'s output. It doesn't:
    /// AssemblyVersion is numeric only, the suffix lives in a
    /// different attribute. Test pinned this with the corrected
    /// 99.99.99 value.</para>
    /// </summary>
    public const string BuildVersion = "99.99.99";

    public static bool IsAvailable => OperatingSystem.IsLinux() && IsDotnetOnPath();

    /// <summary>
    /// Builds the binary if not already cached. Returns the absolute path
    /// to the executable. Idempotent + thread-safe.
    /// </summary>
    public string EnsureBuilt()
    {
        lock (_buildLock)
        {
            if (_cachedBinaryPath != null && File.Exists(_cachedBinaryPath))
                return _cachedBinaryPath;

            var binaryPath = Build();

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
    /// Runs the binary with the given args + a clean environment.
    /// Returns (exitCode, combined stdout+stderr). 60s wall-clock cap —
    /// individual subcommands like <c>version</c> are sub-second; longer
    /// timeouts indicate hangs that should fail-fast.
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

    private static string Build()
    {
        var repoRoot = LocateRepoRoot();
        var csproj = Path.Combine(repoRoot, "src", "Squid.Tentacle", "Squid.Tentacle.csproj");

        if (!File.Exists(csproj))
            throw new FileNotFoundException(
                $"Could not locate Squid.Tentacle.csproj at {csproj}. Repo root resolved to {repoRoot}.");

        // Stable cache path under the test assembly's bin/. Survives between
        // test runs but cleared by `dotnet clean` / `git clean -xdf bin/`.
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var outputDir = Path.Combine(thisAssemblyDir, "squid-tentacle-binary-fixture", "linux-x64");

        Directory.CreateDirectory(outputDir);

        // Mirror production CI's publish flags exactly (build-publish-linux-
        // tentacle.yml line 229+) — single self-contained binary, stamped
        // version. Differences from production:
        //   - Version: pinned to BuildVersion so tests can assert on it
        //   - Configuration: Release (production also uses Release)
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
        psi.ArgumentList.Add("linux-x64");
        psi.ArgumentList.Add("--self-contained");
        psi.ArgumentList.Add("true");
        psi.ArgumentList.Add("-p:PublishSingleFile=true");
        psi.ArgumentList.Add($"-p:Version={BuildVersion}");
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

        return Path.Combine(outputDir, "Squid.Tentacle");
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
