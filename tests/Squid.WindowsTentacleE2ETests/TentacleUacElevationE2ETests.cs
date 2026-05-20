using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Phase 3 (3.6) — pins the UAC self-elevation path of <c>install-tentacle.ps1</c>.
///
/// <para><b>Tier</b>: 🟢 High-fidelity for the structural invariants we CAN
/// observe without a real UAC prompt (a real prompt would block the CI runner
/// forever). Real <c>powershell.exe</c> running real <c>install-tentacle.ps1</c>
/// from disk; <c>Test-IsAdministrator</c> + <c>Start-Process</c> are overridden
/// at <c>global:</c> scope by a wrapper script, redirecting the would-be UAC
/// re-launch into a JSON capture file. Skip-on-non-Windows guard keeps the
/// rest of the dev fleet green.</para>
///
/// <para><b>Production gap closed</b>: <see cref="TentacleInstallScriptResilienceE2ETests"/>
/// covers every SKIP-elevation path (already admin / user-owned dir / explicit
/// <c>-NoAutoElevate</c>). None of those exercise the ACTUAL elevation branch
/// (<c>Invoke-SelfElevation</c>) — so a regression that breaks
/// <c>$childArgs</c> construction (missing <c>-NoProfile</c>,
/// <c>-ExecutionPolicy Bypass</c>, or the <c>-File</c> path) would land in
/// production silently. This test pins those structural invariants.</para>
///
/// <para><b>How the capture works</b>: the wrapper script defines
/// <c>function global:Test-IsAdministrator { return $false }</c> (forcing the
/// elevation branch) and <c>function global:Start-Process</c> (capturing
/// <c>$ArgumentList</c> to a JSON file then returning a fake exit). It then
/// calls <c>install-tentacle.ps1</c> with <c>-InstallDir 'C:\Program Files\Squid Tentacle'</c>
/// — the default — so <c>$needsElevation</c> evaluates to <c>$true</c> and the
/// elevation branch fires.</para>
///
/// <para><b>What's NOT tested here and why</b>: the actual elevated child
/// process never runs (would need a real UAC). Whether the operator's
/// <c>-Version</c> / <c>-InstallDir</c> / <c>-DownloadBase</c> are forwarded to
/// the child is investigated by <c>UacElevation_OperatorArgsForwardedToChild</c>
/// below — see the doc-comment there for the open question about
/// <c>$PSBoundParameters</c> scope inside nested PowerShell functions.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleInstallScript)]
public sealed class TentacleUacElevationE2ETests
{
    [Fact]
    public async Task UacElevation_ChildArgs_ContainsRequiredPowerShellInvocationFlags()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var ctx = new UacCaptureContext();

        var (exitCode, stdout, stderr) = await RunWrapperAsync(ctx);

        // The mocked Start-Process returns ExitCode=99, the script's `exit $childExit`
        // surfaces it. 0/1 here would mean elevation never triggered (test misconfigured).
        exitCode.ShouldBe(99,
            customMessage:
                $"wrapper MUST exit with the mocked Start-Process ExitCode (99). " +
                $"If exit was 0 or 1, the elevation branch in install-tentacle.ps1 didn't fire — " +
                $"check that Test-IsAdministrator override returned $false and that " +
                $"-InstallDir was set to the default path that requires elevation.\n" +
                $"stdout:\n{stdout}\nstderr:\n{stderr}");

        File.Exists(ctx.CapturePath).ShouldBeTrue(
            customMessage:
                $"capture file ({ctx.CapturePath}) MUST exist — Start-Process was overridden to write " +
                $"its $ArgumentList there. If the file is absent, the override didn't fire " +
                $"(check global:-scope function definition in the wrapper). " +
                $"stdout:\n{stdout}\nstderr:\n{stderr}");

        var capturedJson = await File.ReadAllTextAsync(ctx.CapturePath);
        var captured = JsonSerializer.Deserialize<JsonElement>(capturedJson);
        var argumentList = ExtractArgumentList(captured);

        argumentList.ShouldContain("-NoProfile",
            customMessage:
                "elevated child MUST be launched with -NoProfile so the operator's $PROFILE " +
                "(which may have aliases, prompt changes, network drive mounts) doesn't crash " +
                "the unattended install. Regression here = flaky elevated installs.");

        argumentList.ShouldContain("-ExecutionPolicy",
            customMessage:
                "elevated child MUST receive an explicit -ExecutionPolicy. Without it, " +
                "operators with RestrictedExecutionPolicy or AllSigned would silently fail post-UAC.");

        argumentList.ShouldContain("Bypass",
            customMessage:
                "the -ExecutionPolicy value MUST be Bypass. AllSigned / RemoteSigned would " +
                "block the script from running. If you've intentionally tightened the policy, " +
                "update this assertion AND the install-tentacle.ps1 doc-comment in Invoke-SelfElevation.");

        argumentList.ShouldContain("-File",
            customMessage:
                "elevated child MUST run via -File (not via -Command). -Command can mis-quote " +
                "args containing spaces or quotes; -File is the canonical script invocation form.");

        var hasFilePathArg = argumentList.Any(a =>
            a.EndsWith("install-tentacle.ps1", StringComparison.OrdinalIgnoreCase) ||
            a.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase));

        hasFilePathArg.ShouldBeTrue(
            customMessage:
                $"elevated child MUST receive a .ps1 file path. Captured argv: " +
                $"[{string.Join(", ", argumentList.Select(a => $"'{a}'"))}]. " +
                $"If only -NoProfile etc. are present without a script path, $scriptPath in " +
                $"Invoke-SelfElevation is empty — investigate pipe-invocation materialization.");
    }

    [Fact]
    public async Task UacElevation_StartProcess_InvokedWithRunAsVerb()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var ctx = new UacCaptureContext();

        var (exitCode, stdout, stderr) = await RunWrapperAsync(ctx);

        exitCode.ShouldBe(99,
            customMessage: $"elevation branch must fire. stdout:\n{stdout}\nstderr:\n{stderr}");

        var capturedJson = await File.ReadAllTextAsync(ctx.CapturePath);
        var captured = JsonSerializer.Deserialize<JsonElement>(capturedJson);

        captured.TryGetProperty("Verb", out var verbProp).ShouldBeTrue(
            customMessage: "captured Start-Process invocation MUST include a 'Verb' property");

        string.Equals(verbProp.GetString(), "RunAs", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
            customMessage:
                $"Start-Process MUST be invoked with -Verb RunAs to trigger the UAC prompt. " +
                $"Without RunAs, the child process inherits the parent's (non-admin) token " +
                $"and the install will fail with 'Access Denied' on the default install dir. " +
                $"Captured Verb: '{verbProp.GetString()}'.");
    }

    [Fact]
    public async Task UacElevation_StartProcess_InvokedWithPowerShellExe()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var ctx = new UacCaptureContext();

        var (exitCode, stdout, stderr) = await RunWrapperAsync(ctx);

        exitCode.ShouldBe(99,
            customMessage: $"elevation branch must fire. stdout:\n{stdout}\nstderr:\n{stderr}");

        var capturedJson = await File.ReadAllTextAsync(ctx.CapturePath);
        var captured = JsonSerializer.Deserialize<JsonElement>(capturedJson);

        captured.TryGetProperty("FilePath", out var filePathProp).ShouldBeTrue(
            customMessage: "captured Start-Process invocation MUST include a 'FilePath' property");

        var filePath = filePathProp.GetString() ?? "";

        string.Equals(filePath, "powershell.exe", StringComparison.OrdinalIgnoreCase).ShouldBeTrue(
            customMessage:
                $"elevated child MUST be powershell.exe (Windows PowerShell 5.1) — NOT pwsh.exe. " +
                $"pwsh is the .NET-Core-based PowerShell 7+, which is OPTIONAL on Windows hosts. " +
                $"Using pwsh would break elevation on machines that only have built-in PowerShell. " +
                $"Captured FilePath: '{filePath}'.");
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static async Task<(int exitCode, string stdout, string stderr)> RunWrapperAsync(UacCaptureContext ctx)
    {
        var wrapperPath = ctx.WriteWrapperScript(LocateInstallScript());

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(wrapperPath);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch powershell.exe");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                "UAC capture wrapper did not exit within 60s. Likely cause: the wrapper's " +
                "Start-Process override didn't return, or install-tentacle.ps1 entered an " +
                "unexpected branch. Manually run the wrapper from a developer powershell session.");
        }

        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    private static List<string> ExtractArgumentList(JsonElement captured)
    {
        if (!captured.TryGetProperty("ArgumentList", out var arr))
            return new List<string>();

        var result = new List<string>();

        // ArgumentList may serialize as either a JSON array or a single string,
        // depending on how PowerShell's ConvertTo-Json handled it. Handle both.
        if (arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
        }
        else if (arr.ValueKind == JsonValueKind.String)
        {
            result.Add(arr.GetString() ?? "");
        }

        return result;
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

        throw new FileNotFoundException(
            "Could not locate deploy/scripts/install-tentacle.ps1 from the test assembly's " +
            "directory tree. Verify the file still exists and is included in the test " +
            "project's content/copy-on-build settings.");
    }

    private sealed class UacCaptureContext : IDisposable
    {
        public string CapturePath { get; }
        public string WrapperPath { get; private set; } = "";

        public UacCaptureContext()
        {
            CapturePath = Path.Combine(Path.GetTempPath(),
                $"squid-uac-capture-{Guid.NewGuid():N}.json");
        }

        public string WriteWrapperScript(string installScriptPath)
        {
            WrapperPath = Path.Combine(Path.GetTempPath(),
                $"squid-uac-wrapper-{Guid.NewGuid():N}.ps1");

            // PowerShell wrapper that:
            //   1. Replaces Test-IsAdministrator with a stub returning $false
            //      (forcing the $needsElevation branch in install-tentacle.ps1)
            //   2. Replaces Start-Process with a stub that captures $ArgumentList
            //      (+ FilePath + Verb) to a JSON file and returns a fake proc with
            //      ExitCode=99 (so we can confirm elevation actually fired)
            //   3. Invokes install-tentacle.ps1 with -InstallDir set to the DEFAULT
            //      path ("C:\Program Files\Squid Tentacle") so $needsElevation is true.
            //
            // global: scope on the function overrides is REQUIRED — install-tentacle.ps1
            // is a separate script with its own scope, so plain function overrides
            // wouldn't be visible there.
            var script = $@"
$ErrorActionPreference = 'Stop'

# Override 1: pretend we're never admin -> forces $needsElevation = $true in
# install-tentacle.ps1's `$needsElevation = ($InstallDir -eq $DefaultInstallDir) -and -not (Test-IsAdministrator)`.
function global:Test-IsAdministrator {{ return $false }}

# Override 2: capture the elevation invocation instead of actually triggering UAC.
# install-tentacle.ps1's Invoke-SelfElevation calls:
#   Start-Process powershell.exe -Verb RunAs -ArgumentList $childArgs -Wait -PassThru
function global:Start-Process {{
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)] [string] $FilePath,
        [string] $Verb,
        [object[]] $ArgumentList,
        [switch] $Wait,
        [switch] $PassThru
    )

    $captured = @{{
        FilePath     = $FilePath
        Verb         = $Verb
        ArgumentList = $ArgumentList
    }}
    $captured | ConvertTo-Json -Depth 5 | Out-File -FilePath '{ctx_CapturePathEscaped(CapturePath)}' -Encoding UTF8

    # Return a fake process with ExitCode=99 so the wrapper test can confirm
    # the elevation branch actually fired (not just no-op'd).
    return [pscustomobject]@{{ ExitCode = 99 }}
}}

# Drive install-tentacle.ps1 with -InstallDir pointed at the default path so
# `$needsElevation` is $true. -DownloadBase is required but won't be used
# because Start-Process is mocked + the script exits before download.
& '{ctx_CapturePathEscaped(installScriptPath)}' `
    -Version '9.9.9-test' `
    -InstallDir 'C:\Program Files\Squid Tentacle' `
    -DownloadBase 'https://example.invalid/dl'
";

            File.WriteAllText(WrapperPath, script);
            return WrapperPath;
        }

        public void Dispose()
        {
            try { if (File.Exists(WrapperPath)) File.Delete(WrapperPath); } catch { /* best-effort */ }
            try { if (File.Exists(CapturePath)) File.Delete(CapturePath); } catch { /* best-effort */ }
        }

        // PowerShell single-quoted strings don't process escape sequences, so we
        // only need to double any existing single-quotes in paths. Windows paths
        // don't typically contain single-quotes, but be defensive.
        private static string ctx_CapturePathEscaped(string path) => path.Replace("'", "''");
    }
}
