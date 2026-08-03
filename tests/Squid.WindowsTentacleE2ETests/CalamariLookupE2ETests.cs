using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// E2E coverage for the Calamari lookup fix: when <c>squid-calamari.exe</c>
/// is installed beside <c>Squid.Tentacle.exe</c> (recorded via
/// <c>install-info.json</c>), the DeployByCalamari resolution MUST pick the
/// sibling via absolute path — even when <c>squid-calamari.exe</c> is NOT in
/// PATH.
///
/// <para><b>Tier</b>: 🟢 High-fidelity. Real <c>install-tentacle.ps1</c>
/// against a <see cref="LocalReleaseMirror"/> serving a pre-built zip that
/// contains both binaries, then a real <c>powershell.exe</c> process runs the
/// rendered, embedded <c>DeployByCalamari.ps1</c> template and invokes the
/// resolved shim. The shim (a real PE executable built by the
/// CalamariShim project) writes a marker file so the test can assert the
/// absolute path was used.</para>
///
/// <para><b>Windows-only</b>: requires <c>install-tentacle.ps1</c> and a real
/// Windows PowerShell host. Non-Windows hosts no-op via
/// <c>if (!OperatingSystem.IsWindows()) return;</c>.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.CalamariLookup)]
public sealed class CalamariLookupE2ETests : IDisposable
{
    private readonly string _programData =
        Path.Combine(Path.GetTempPath(), $"squid-calamari-pd-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_programData)) Directory.Delete(_programData, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task DeployByCalamari_BundledCalamariBesideTentacle_ResolvedViaAbsolutePath()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mirror = LocalReleaseMirror.Start();
        using var ctx = new InstallDirContext();

        // Build a zip containing both the (fake) Tentacle binary and the real
        // squid-calamari.exe shim, and stage it as the release archive.
        var shimPath = LocateCalamariShim();
        var zipBytes = BuildReleaseZip(shimPath);
        mirror.StagePreBuiltArchive(zipBytes);

        // Install Tentacle with -NoServiceInstall; install-info.json is written
        // to the test-isolated %ProgramData% with BinaryPath = installDir.
        var (exitCode, stdout, stderr) = await RunInstallScriptAsync(
            "-Version", "1.6.0-test",
            "-InstallDir", ctx.InstallDir,
            "-DownloadBase", mirror.BaseUri.ToString().TrimEnd('/'),
            "-NoServiceInstall"
        );

        exitCode.ShouldBe(0,
            customMessage: $"install-tentacle.ps1 MUST exit 0. stdout:\n{stdout}\nstderr:\n{stderr}");

        // Both binaries must be present beside each other in the install dir.
        var tentacleExe = Path.Combine(ctx.InstallDir, "Squid.Tentacle.exe");
        var calamariExe = Path.Combine(ctx.InstallDir, "squid-calamari.exe");
        File.Exists(tentacleExe).ShouldBeTrue(
            customMessage: $"Squid.Tentacle.exe MUST exist at {tentacleExe}. stdout:\n{stdout}");
        File.Exists(calamariExe).ShouldBeTrue(
            customMessage: $"squid-calamari.exe MUST exist at {calamariExe}. stdout:\n{stdout}");

        // install-info.json must point at the installed Tentacle binary so the
        // resolution logic can derive the sibling calamari path.
        var installInfoPath = Path.Combine(_programData, "Squid", "Tentacle", "install-info.json");
        File.Exists(installInfoPath).ShouldBeTrue(
            customMessage: $"install-info.json MUST exist at {installInfoPath}. stdout:\n{stdout}");

        // Render and run the actual embedded production template. The child
        // PATH deliberately excludes the install dir and every directory that
        // contains squid-calamari.exe, so only the install-info.json sibling
        // lookup can succeed.
        var markerPath = Path.Combine(ctx.InstallDir, "calamari-marker.txt");
        var resolutionScript = Path.Combine(ctx.InstallDir, "resolve-calamari.ps1");
        var packagePath = Path.Combine(ctx.InstallDir, "package.yaml");
        var variablesPath = Path.Combine(ctx.InstallDir, "variables.json");
        var sensitiveVariablesPath = Path.Combine(ctx.InstallDir, "sensitive-variables.json");
        await File.WriteAllTextAsync(packagePath, "apiVersion: v1", Encoding.UTF8);
        await File.WriteAllTextAsync(variablesPath, "{}", Encoding.UTF8);
        await File.WriteAllTextAsync(sensitiveVariablesPath, "{}", Encoding.UTF8);

        Directory.CreateDirectory(ctx.ToolsDirectory);
        await File.WriteAllTextAsync(Path.Combine(ctx.ToolsDirectory, "kubectl.cmd"), "@exit /b 0", Encoding.ASCII);
        await File.WriteAllTextAsync(Path.Combine(ctx.ToolsDirectory, "bash.cmd"), "@exit /b 0", Encoding.ASCII);

        var payload = new CalamariPayload
        {
            TemplateBody = UtilService.GetEmbeddedScriptContent("DeployByCalamari.ps1"),
            SensitivePassword = "test-sensitive-password"
        };
        var renderedTemplate = payload.FillTemplate(packagePath, variablesPath, sensitiveVariablesPath);
        renderedTemplate.ShouldNotContain("{{", customMessage: "rendered production template must not retain placeholders");
        await File.WriteAllTextAsync(resolutionScript, renderedTemplate, Encoding.UTF8);

        var (resExit, resOut, resErr) = await RunPowerShellAsync(
            resolutionScript,
            excludeDirFromPath: ctx.InstallDir,
            prependPathEntry: ctx.ToolsDirectory,
            markerPath: markerPath);

        resExit.ShouldBe(0,
            customMessage: $"resolution script MUST exit 0. stdout:\n{resOut}\nstderr:\n{resErr}");

        File.Exists(markerPath).ShouldBeTrue(
            customMessage: $"calamari marker MUST exist at {markerPath}. stdout:\n{resOut}\nstderr:\n{resErr}");

        var marker = await File.ReadAllTextAsync(markerPath);
        marker.ShouldContain("ProcessPath", customMessage: $"unexpected marker content:\n{marker}");
        marker.ShouldContain("apply-yaml", customMessage: $"calamari args must be forwarded:\n{marker}");
        marker.ShouldContain($"--file={packagePath}", customMessage: $"package path must be rendered into calamari args:\n{marker}");
        marker.ShouldContain($"--variables={variablesPath}", customMessage: $"variables path must be rendered into calamari args:\n{marker}");
        marker.ShouldContain(Path.GetFullPath(calamariExe),
            customMessage: $"shim must be invoked via absolute path. marker:\n{marker}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static byte[] BuildReleaseZip(string shimExePath)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The shim is a framework-dependent apphost: without its sibling
            // .dll / .runtimeconfig.json / .deps.json it cannot start after
            // extraction, so bundle the whole build output like the other
            // Windows E2E fixtures do.
            var shimOutputDir = Path.GetDirectoryName(shimExePath)!;
            foreach (var sourceFile in Directory.EnumerateFiles(shimOutputDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(shimOutputDir, sourceFile);
                var entry = zip.CreateEntry(relativePath, CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(sourceFile);
                fileStream.CopyTo(entryStream);
            }

            var tentacleEntry = zip.CreateEntry("Squid.Tentacle.exe", CompressionLevel.Fastest);
            using (var writer = new StreamWriter(tentacleEntry.Open(), Encoding.UTF8))
                writer.Write("# fake Squid.Tentacle.exe for E2E test\n");
        }

        return ms.ToArray();
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunInstallScriptAsync(params string[] scriptArgs)
    {
        var scriptPath = LocateInstallScript();
        return await RunPowerShellAsync(
            scriptPath,
            additionalArgs: scriptArgs);
    }

    private async Task<(int exitCode, string stdout, string stderr)> RunPowerShellAsync(
        string scriptPath,
        string[]? additionalArgs = null,
        string? excludeDirFromPath = null,
        string? prependPathEntry = null,
        string? markerPath = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Redirect %ProgramData% so install-info.json lands in the isolated dir.
        psi.EnvironmentVariables["ProgramData"] = _programData;

        if (markerPath != null)
        {
            psi.EnvironmentVariables["SQUID_CALAMARI_E2E_MARKER"] = markerPath;
        }

        // Keep the commands required by the real template available, but
        // remove the Tentacle directory and any PATH-provided Calamari binary.
        if (excludeDirFromPath != null || prependPathEntry != null)
        {
            var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().Trim('"'))
                .Where(p => excludeDirFromPath == null ||
                    !string.Equals(p.TrimEnd('\\', '/'), excludeDirFromPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                .Where(p => !File.Exists(Path.Combine(p, "squid-calamari.exe")));

            if (prependPathEntry != null)
                pathEntries = new[] { prependPathEntry }.Concat(pathEntries);

            psi.EnvironmentVariables["PATH"] = string.Join(';', pathEntries);
        }

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        if (additionalArgs != null)
            foreach (var arg in additionalArgs) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch powershell.exe");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"PowerShell script did not exit within 60s: {scriptPath}");
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

    private static string LocateCalamariShim()
    {
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var dir = thisAssemblyDir;

        for (var i = 0; i < 8 && dir != null; i++)
        {
            var shimProjectDir = Path.Combine(dir, "tests", "Squid.WindowsTentacleE2E.CalamariShim");
            if (Directory.Exists(shimProjectDir))
            {
                foreach (var config in new[] { "Release", "Debug" })
                {
                    var candidate = Path.Combine(
                        shimProjectDir, "bin", config, "net9.0", "squid-calamari.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(
            "Could not locate squid-calamari.exe shim. Build the CalamariShim project first.");
    }

    private sealed class InstallDirContext : IDisposable
    {
        public string InstallDir { get; } =
            Path.Combine(Path.GetTempPath(), $"squid-calamari-install-{Guid.NewGuid():N}");

        public string ToolsDirectory { get; } =
            Path.Combine(Path.GetTempPath(), $"squid-calamari-tools-{Guid.NewGuid():N}");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(InstallDir))
                    Directory.Delete(InstallDir, recursive: true);
            }
            catch { /* best-effort */ }

            try
            {
                if (Directory.Exists(ToolsDirectory))
                    Directory.Delete(ToolsDirectory, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}
