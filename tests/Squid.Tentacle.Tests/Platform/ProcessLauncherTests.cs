using System.Globalization;
using System.Text;
using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Platform;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Collections;
using Xunit;

namespace Squid.Tentacle.Tests.Platform;

/// <summary>
/// pin the cross-platform process-launcher abstraction.
///
/// <para><b>Why this exists</b>:  the agent had per-syntax
/// process-launching code split across two private static helpers in
/// <c>LocalScriptService</c> (<c>StartBashProcess</c> + <c>StartPwshProcess</c>),
/// each with the same five-line PSI shape duplicated. Windows Tentacle support
/// needs a third launcher targeting <c>PowerShell.exe</c> (with OEM stdout
/// decoding), so the right move is to consolidate onto a single typed contract
/// — <see cref="IProcessLauncher"/> — that the Phase-B.2 Windows impl can plug
/// into without touching <c>LocalScriptService</c> internals.</para>
///
/// <para><b>What this pins</b>: the EXACT PSI shape (FileName, ArgumentList,
/// redirect flags, encoding) that the existing real-bash and real-pwsh tests
/// in <c>LocalScriptServiceTests</c> + <c>WindowsPowerShellE2ETests</c> rely
/// on byte-for-byte. If a future refactor accidentally drops <c>-File</c>,
/// reorders ArgumentList, or flips encoding to OEM, these unit tests catch it
/// without a full E2E run.</para>
/// </summary>
[Collection(TentacleEnvVarMutatorsCollection.Name)]
[Trait("Category", TentacleTestCategories.Core)]
public sealed class ProcessLauncherTests
{
    // ── BashProcessLauncher PSI shape — pinned bit-for-bit from  ───

    [Fact]
    public void Bash_BuildStartInfo_FileNameIsBash()
    {
        // The literal "bash" must stay on PATH; renaming would break every
        // existing Linux/macOS deploy. Tests here defend the contract.
        new BashProcessLauncher().BuildStartInfo("/tmp/x", Array.Empty<string>())
            .FileName.ShouldBe("bash");
    }

    [Fact]
    public void Bash_BuildStartInfo_ArgvIsScriptShThenUserArgs()
    {
        var psi = new BashProcessLauncher().BuildStartInfo("/tmp/x", new[] { "alpha", "beta" });

        // First positional arg must be the bundle's script.sh — bash invokes
        // it as the script body. User args follow as $1, $2 etc.
        psi.ArgumentList.Count.ShouldBe(3);
        psi.ArgumentList[0].ShouldBe("script.sh");
        psi.ArgumentList[1].ShouldBe("alpha");
        psi.ArgumentList[2].ShouldBe("beta");
    }

    [Fact]
    public void Bash_BuildStartInfo_NullUserArgs_StillJustScriptSh()
    {
        // Defensive — caller passes null when StartScriptCommand.Arguments is null.
        var psi = new BashProcessLauncher().BuildStartInfo("/tmp/x", null!);

        psi.ArgumentList.Count.ShouldBe(1);
        psi.ArgumentList[0].ShouldBe("script.sh");
    }

    [Fact]
    public void Bash_BuildStartInfo_RedirectFlagsAndUtf8Encoding()
    {
        var psi = new BashProcessLauncher().BuildStartInfo("/tmp/x", Array.Empty<string>());

        // Redirected so the output pump can capture stdout/stderr per-line.
        psi.RedirectStandardOutput.ShouldBeTrue();
        psi.RedirectStandardError.ShouldBeTrue();

        // UTF-8 explicitly so non-ASCII output round-trips. Bash inherits the
        // host LANG; pinning UTF-8 here defends against C-locale hosts.
        psi.StandardOutputEncoding.ShouldBe(Encoding.UTF8);
        psi.StandardErrorEncoding.ShouldBe(Encoding.UTF8);

        // UseShellExecute MUST be false — we're not asking Windows to find a
        // shell verb, we're invoking bash directly.
        psi.UseShellExecute.ShouldBeFalse();

        // No console window flicker on Windows hosts.
        psi.CreateNoWindow.ShouldBeTrue();

        // Pinned working dir.
        psi.WorkingDirectory.ShouldBe("/tmp/x");
    }

    // ── PwshCoreProcessLauncher PSI shape — same contract as Bash but for pwsh ─

    [Fact]
    public void PwshCore_BuildStartInfo_FileNameIsPwsh()
    {
        new PwshCoreProcessLauncher().BuildStartInfo("/tmp/x", Array.Empty<string>())
            .FileName.ShouldBe("pwsh");
    }

    [Fact]
    public void PwshCore_BuildStartInfo_ArgvIsDashFileScriptPs1ThenUserArgs()
    {
        var psi = new PwshCoreProcessLauncher().BuildStartInfo("/tmp/x", new[] { "alpha" });

        // The -File form is what the launcher used.
        // Drift here would change how pwsh interprets the script (e.g.
        // -Command would change exit-code propagation semantics).
        psi.ArgumentList.Count.ShouldBe(3);
        psi.ArgumentList[0].ShouldBe("-File");
        psi.ArgumentList[1].ShouldBe("script.ps1");
        psi.ArgumentList[2].ShouldBe("alpha");
    }

    [Fact]
    public void PwshCore_BuildStartInfo_RedirectFlagsAndUtf8Encoding()
    {
        var psi = new PwshCoreProcessLauncher().BuildStartInfo("/tmp/x", Array.Empty<string>());

        psi.RedirectStandardOutput.ShouldBeTrue();
        psi.RedirectStandardError.ShouldBeTrue();
        psi.StandardOutputEncoding.ShouldBe(Encoding.UTF8);
        psi.StandardErrorEncoding.ShouldBe(Encoding.UTF8);
        psi.UseShellExecute.ShouldBeFalse();
        psi.CreateNoWindow.ShouldBeTrue();
        psi.WorkingDirectory.ShouldBe("/tmp/x");
    }

    // ── Constants pin (Rule 8 — rename detection) ──────────────────────────────

    [Fact]
    public void Bash_ScriptFileName_ConstantPinned()
    {
        // The bundle authoring layer (BashRuntimeBundle) writes "script.sh"
        // into workDir. Renaming this constant would silently desync the
        // launcher from the bundle author and bash would fail to find the file.
        BashProcessLauncher.ScriptFileName.ShouldBe("script.sh");
    }

    [Fact]
    public void PwshCore_ScriptFileName_ConstantPinned()
    {
        PwshCoreProcessLauncher.ScriptFileName.ShouldBe("script.ps1");
    }

    // ── ProcessLauncherFactory routing ──────────────────────────────────────────

    [Fact]
    public void Factory_Bash_ResolvesToBashLauncher()
    {
        ProcessLauncherFactory.Resolve(ScriptType.Bash).ShouldBeOfType<BashProcessLauncher>();
    }

    [Fact]
    public void Factory_PowerShell_ResolvesToPwshCoreLauncher_OnNonWindows()
    {
        // : PowerShell always routes to pwsh-Core.
        // will override this on Windows hosts to route to WindowsPowerShellProcessLauncher.
        // This test pins the B.1 baseline and will be amended in B.2.
        if (OperatingSystem.IsWindows()) return;

        ProcessLauncherFactory.Resolve(ScriptType.PowerShell).ShouldBeOfType<PwshCoreProcessLauncher>();
    }

    [Theory]
    [InlineData(ScriptType.Python)]
    [InlineData(ScriptType.CSharp)]
    [InlineData(ScriptType.FSharp)]
    public void Factory_Unsupported_Throws(ScriptType syntax)
    {
        // Python/CSharp/FSharp intentionally stay on LocalScriptService's
        // inline Start*Process path (no Windows variance worth abstracting).
        // Calamari uses BuildCalamariProcessStartInfo. Throwing here makes
        // any future accidental routing through the factory a compile-or-test
        // visible failure.
        Should.Throw<NotSupportedException>(() => ProcessLauncherFactory.Resolve(syntax))
            .Message.ShouldContain(syntax.ToString());
    }

    // ── WindowsPowerShellProcessLauncher PSI shape ──────────────
    //
    // The actual point of Phase B: PowerShell.exe + OEM stdout decoding so
    // non-ASCII script output (中文 / emoji / accented chars) round-trips
    // through the captured-log layer instead of being mangled by Windows
    // PowerShell 5.1's OEM-codepage default vs .NET's UTF-8 default mismatch.

    [Fact]
    public void WindowsPowerShell_BuildStartInfo_ArgvLiteralPin()
    {
        // The EXACT argv shape that PowerShell.exe sees. Drift here changes
        // semantics in subtle ways: dropping -NonInteractive lets the script
        // hang on a hidden prompt forever; flipping -ExecutionPolicy to
        // Restricted blocks every fresh deploy; using -Command instead of
        // -File would change exit-code propagation. Pin the literal.
        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416  // platform-gated above
            var psi = new WindowsPowerShellProcessLauncher().BuildStartInfo("/tmp/x", new[] { "alpha", "beta" });
#pragma warning restore CA1416

            psi.ArgumentList.Count.ShouldBe(9);
            psi.ArgumentList[0].ShouldBe("-NoProfile");
            psi.ArgumentList[1].ShouldBe("-NoLogo");
            psi.ArgumentList[2].ShouldBe("-NonInteractive");
            psi.ArgumentList[3].ShouldBe("-ExecutionPolicy");
            psi.ArgumentList[4].ShouldBe("Unrestricted");
            psi.ArgumentList[5].ShouldBe("-File");
            psi.ArgumentList[6].ShouldBe("script.ps1");
            psi.ArgumentList[7].ShouldBe("alpha");
            psi.ArgumentList[8].ShouldBe("beta");
            return;
        }

        // On non-Windows test runners we still want to verify the PSI shape
        // (the launcher's BuildStartInfo doesn't actually call any Windows
        // APIs that throw on Linux — it just does Encoding.GetEncoding which
        // falls back to UTF-8). [SupportedOSPlatform("windows")] is a hint to
        // the analyzer, not a runtime gate.
#pragma warning disable CA1416
        var psiNonWin = new WindowsPowerShellProcessLauncher().BuildStartInfo("/tmp/x", new[] { "alpha", "beta" });
#pragma warning restore CA1416

        psiNonWin.ArgumentList.Count.ShouldBe(9);
        psiNonWin.ArgumentList[0].ShouldBe("-NoProfile");
        psiNonWin.ArgumentList[1].ShouldBe("-NoLogo");
        psiNonWin.ArgumentList[2].ShouldBe("-NonInteractive");
        psiNonWin.ArgumentList[3].ShouldBe("-ExecutionPolicy");
        psiNonWin.ArgumentList[4].ShouldBe("Unrestricted");
        psiNonWin.ArgumentList[5].ShouldBe("-File");
        psiNonWin.ArgumentList[6].ShouldBe("script.ps1");
        psiNonWin.ArgumentList[7].ShouldBe("alpha");
        psiNonWin.ArgumentList[8].ShouldBe("beta");
    }

    [Fact]
    public void WindowsPowerShell_BuildStartInfo_FileNameEndsWithPowerShellExe()
    {
        // Two valid resolutions: full canonical path on real Windows, bare
        // "PowerShell.exe" on PATH-fallback or non-Windows hosts. Both end
        // with "PowerShell.exe" (case-sensitive — matches what we emit).
#pragma warning disable CA1416
        var psi = new WindowsPowerShellProcessLauncher().BuildStartInfo("/tmp/x", Array.Empty<string>());
#pragma warning restore CA1416

        psi.FileName.ShouldEndWith("PowerShell.exe");
    }

    [Fact]
    public void WindowsPowerShell_BuildStartInfo_RedirectFlagsAndCreateNoWindow()
    {
#pragma warning disable CA1416
        var psi = new WindowsPowerShellProcessLauncher().BuildStartInfo("/tmp/x", Array.Empty<string>());
#pragma warning restore CA1416

        psi.RedirectStandardOutput.ShouldBeTrue();
        psi.RedirectStandardError.ShouldBeTrue();
        psi.UseShellExecute.ShouldBeFalse();
        psi.CreateNoWindow.ShouldBeTrue();
        psi.WorkingDirectory.ShouldBe("/tmp/x");
    }

    [Fact]
    public void WindowsPowerShell_BuildStartInfo_NullUserArgs_StillJustBaseFlags()
    {
        // Caller passes null when StartScriptCommand.Arguments is null —
        // null-safe defensive path.
#pragma warning disable CA1416
        var psi = new WindowsPowerShellProcessLauncher().BuildStartInfo("/tmp/x", null!);
#pragma warning restore CA1416

        psi.ArgumentList.Count.ShouldBe(7);
        psi.ArgumentList[6].ShouldBe("script.ps1");
    }

    [Fact]
    public void WindowsPowerShell_StdoutEncoding_IsOemOnWindows_NotUtf8()
    {
        // The whole point of having a separate Windows launcher: PowerShell.exe
        // writes OEM bytes, not UTF-8. Setting StandardOutputEncoding to OEM
        // lets the StreamReader decode non-ASCII output correctly. Without
        // this, every 中文 / emoji / accented char would be mangled by .NET's
        // default UTF-8 decoder reading OEM bytes. Windows-only because OEM
        // codepage discovery is locale-dependent on Windows; falls back to
        // UTF-8 on non-Windows test runners (which is fine — prod is Windows).
        if (!OperatingSystem.IsWindows()) return;

#pragma warning disable CA1416
        var psi = new WindowsPowerShellProcessLauncher().BuildStartInfo("/tmp/x", Array.Empty<string>());
#pragma warning restore CA1416

        var expectedOemCodePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;

        psi.StandardOutputEncoding.ShouldNotBeNull();
        psi.StandardOutputEncoding!.CodePage.ShouldBe(expectedOemCodePage);
        psi.StandardErrorEncoding.ShouldNotBeNull();
        psi.StandardErrorEncoding!.CodePage.ShouldBe(expectedOemCodePage);

        // Defence-in-depth: explicitly assert NOT UTF-8 (codepage 65001).
        psi.StandardOutputEncoding.CodePage.ShouldNotBe(Encoding.UTF8.CodePage);
    }

    [Fact]
    public void WindowsPowerShell_ScriptFileName_ConstantPinned()
    {
        // The bundle authoring layer writes "script.ps1" into workDir.
        // Renaming this constant would silently desync the launcher from
        // the bundle author and PowerShell.exe would fail to find the file.
        WindowsPowerShellProcessLauncher.ScriptFileName.ShouldBe("script.ps1");
    }

    [Fact]
    public void WindowsPowerShell_FallbackFileName_ConstantPinned()
    {
        // PATH-fallback used when canonical System32 path doesn't exist
        // (Nano Server, custom install). Pinning the literal makes any
        // accidental rename like "powershell.exe" (lowercase, broken on
        // case-sensitive filesystems) a compile/test visible failure.
        WindowsPowerShellProcessLauncher.FallbackFileName.ShouldBe("PowerShell.exe");
    }

    [Fact]
    public void Factory_PowerShell_OnWindows_DefaultRoutesToPwshCore_NoBreakingChange()
    {
        // preserve behaviour: PowerShell on Windows
        // routes to PwshCoreProcessLauncher by default. Switching to
        // WindowsPowerShellProcessLauncher requires the operator to set
        // SQUID_TENTACLE_USE_WINDOWS_POWERSHELL=true (see env-var-gate test
        // below). Default-off is essential because Windows PowerShell 5.1's
        // OEM stdout would silently mangle cross-locale Unicode (e.g. 你好
        // on en-US runners), which would break the existing
        // Pwsh_UnicodeOutput_EncodedCorrectly E2E test.
        if (!OperatingSystem.IsWindows()) return;

        var prior = Environment.GetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, null);

            ProcessLauncherFactory.Resolve(ScriptType.PowerShell)
                .ShouldBeOfType<PwshCoreProcessLauncher>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, prior);
        }
    }

    [Fact]
    public void Factory_PowerShell_OnWindows_OptInRoutesToWindowsImpl()
    {
        // With the env var set, factory routes to the OEM-decoding,
        // PowerShell.exe-targeted launcher. Non-Windows hosts ignore the
        // env var (PowerShell.exe doesn't exist there) — covered by
        // Factory_PowerShell_ResolvesToPwshCoreLauncher_OnNonWindows.
        if (!OperatingSystem.IsWindows()) return;

        var prior = Environment.GetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, "true");

            ProcessLauncherFactory.Resolve(ScriptType.PowerShell)
                .ShouldBeOfType<WindowsPowerShellProcessLauncher>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, prior);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("YES", true)]
    // Audit-fix A2: tolerate operator typos like `set FOO= true` (cmd.exe leaks
    // the leading space into the env var value). Trim before compare.
    [InlineData("  true  ", true)]
    [InlineData("\ttrue\t", true)]
    [InlineData("  yes  ", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("anything-else", false)]
    public void IsWindowsPowerShellPreferred_AcceptsPermissiveTruthyValues(string raw, bool expected)
    {
        // Permissive parsing — operators may set the env var to any common
        // truthy form (1 / true / yes / on, case-insensitive). Anything else
        // — including unset, empty, whitespace, or unrecognised strings —
        // is treated as false. Pinning the contract here makes any future
        // tightening of the parser surface in tests.
        var prior = Environment.GetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, raw);

            ProcessLauncherFactory.IsWindowsPowerShellPreferred().ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, prior);
        }
    }

    [Fact]
    public void UseWindowsPowerShellEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks every air-gapped operator who has
        // baked the literal env var name into their systemd unit drop-in /
        // sc.exe start config. Hard-pin per Rule 8 so a rename becomes a
        // compile-time-visible decision rather than an invisible refactor.
        ProcessLauncherFactory.UseWindowsPowerShellEnvVar.ShouldBe("SQUID_TENTACLE_USE_WINDOWS_POWERSHELL");
    }

    // ── pwsh-Core auto-fallback (operator bug fix) ──────────────────────────────
    //
    // The operator-reported failure mode: a Windows tentacle WITHOUT PowerShell 7
    // installed dispatched a PowerShell upgrade script and crashed with
    // Win32Exception (2) "系统找不到指定的文件" / "system cannot find the file
    // specified". pwsh-Core is OPTIONAL on Windows (Microsoft does not bundle it
    // with the OS); the factory's prior unconditional `pwsh` default stranded
    // every stock Windows host.
    //
    // Fix: probe pwsh.exe availability; when absent, fall back to the always-
    // present OS-bundled PowerShell.exe 5.1. Tests below pin each cell of the
    // truth table:
    //   (isWindows, optInToWindowsPowerShell, pwshAvailable) → expected launcher

    [Fact]
    public void Factory_PowerShell_OnWindows_PwshMissing_AutoFallsBackToWindowsPowerShell()
    {
        // The operator's specific scenario: Windows host, no PS7 installed,
        // no env-var opt-in set. Without auto-fallback, the dispatch hits
        // Win32Exception (2) at process spawn. With auto-fallback, the agent
        // transparently uses Windows PowerShell 5.1 (always present on every
        // supported Windows release since 2016) and the upgrade succeeds.
        if (!OperatingSystem.IsWindows()) return;

        var priorEnv = Environment.GetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar);
        var priorProbe = ProcessLauncherFactory.PwshAvailableProbe;

        try
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, null);
            ProcessLauncherFactory.PwshAvailableProbe = () => false;    // simulate "no pwsh.exe on host"

            ProcessLauncherFactory.Resolve(ScriptType.PowerShell)
                .ShouldBeOfType<WindowsPowerShellProcessLauncher>(
                    customMessage: "Windows host without pwsh-Core MUST auto-fall-back to PowerShell.exe 5.1 — " +
                                   "without this branch, every stock Windows tentacle crashed with Win32Exception (2) " +
                                   "on the first PowerShell dispatch (upgrade, deployment, health check).");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, priorEnv);
            ProcessLauncherFactory.PwshAvailableProbe = priorProbe;
        }
    }

    [Fact]
    public void Factory_PowerShell_OnWindows_PwshAvailable_PrefersPwshCore()
    {
        // Inverse of the auto-fallback case: when pwsh-Core IS resolvable on
        // the host, the factory MUST keep preferring it (UTF-8 stdout,
        // cross-locale Unicode round-trip). Pinning this row ensures a future
        // refactor that accidentally widens the fallback branch to "always use
        // Windows PowerShell on Windows" would fail this test.
        if (!OperatingSystem.IsWindows()) return;

        var priorEnv = Environment.GetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar);
        var priorProbe = ProcessLauncherFactory.PwshAvailableProbe;

        try
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, null);
            ProcessLauncherFactory.PwshAvailableProbe = () => true;

            ProcessLauncherFactory.Resolve(ScriptType.PowerShell)
                .ShouldBeOfType<PwshCoreProcessLauncher>(
                    customMessage: "When pwsh-Core IS available on the Windows host, the factory MUST prefer it. " +
                                   "The auto-fallback branch only fires when the probe returns false.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, priorEnv);
            ProcessLauncherFactory.PwshAvailableProbe = priorProbe;
        }
    }

    [Fact]
    public void Factory_PowerShell_OnWindows_EnvVarOptIn_BeatsAutoFallback_EvenWhenPwshAvailable()
    {
        // Operator opt-in is the explicit user signal "I want Windows PowerShell
        // 5.1 specifically" (e.g. legacy WMI modules, OEM stdout pinning, or
        // forcing test reproducibility). It MUST take precedence over the
        // pwsh-availability probe — otherwise an operator who installs PS7
        // would silently lose their 5.1 routing without any visible warning.
        if (!OperatingSystem.IsWindows()) return;

        var priorEnv = Environment.GetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar);
        var priorProbe = ProcessLauncherFactory.PwshAvailableProbe;

        try
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, "true");
            ProcessLauncherFactory.PwshAvailableProbe = () => true;    // pwsh IS available but opt-in must still win

            ProcessLauncherFactory.Resolve(ScriptType.PowerShell)
                .ShouldBeOfType<WindowsPowerShellProcessLauncher>(
                    customMessage: "Env var opt-in (UseWindowsPowerShellEnvVar=true) MUST beat the pwsh-availability probe. " +
                                   "Operators who explicitly request PS 5.1 get it deterministically, regardless of PS7 install state.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProcessLauncherFactory.UseWindowsPowerShellEnvVar, priorEnv);
            ProcessLauncherFactory.PwshAvailableProbe = priorProbe;
        }
    }

    [Fact]
    public void Factory_PowerShell_OnNonWindows_PwshProbe_DoesNotGate()
    {
        // Off-Windows drift detector: even if the probe somehow returns false
        // on a Linux/macOS host, the factory MUST keep routing to pwsh-Core.
        // Windows PowerShell 5.1 doesn't exist outside Windows; activating the
        // fallback off-Windows would route to a launcher whose binary can never
        // be found. This guard means a future refactor that drops the
        // `OperatingSystem.IsWindows()` gate would fail loudly here.
        if (OperatingSystem.IsWindows()) return;

        var priorProbe = ProcessLauncherFactory.PwshAvailableProbe;

        try
        {
            ProcessLauncherFactory.PwshAvailableProbe = () => false;

            ProcessLauncherFactory.Resolve(ScriptType.PowerShell)
                .ShouldBeOfType<PwshCoreProcessLauncher>(
                    customMessage: "Off-Windows: pwsh-availability probe MUST NOT gate launcher choice — " +
                                   "Linux/macOS always route to pwsh-Core (PowerShell.exe 5.1 doesn't exist there).");
        }
        finally
        {
            ProcessLauncherFactory.PwshAvailableProbe = priorProbe;
        }
    }

    [Fact]
    public void DefaultPwshAvailable_ReturnsBoolean_DoesNotThrow_OnAnyPlatform()
    {
        // Sanity: the default probe must be safe on every supported runner —
        // malformed PATH entries, locked directories, empty %ProgramFiles%,
        // non-Windows hosts all must be handled without throwing. Cold-call
        // the probe and verify it returns one of {true, false} cleanly.
        var result = ProcessLauncherFactory.DefaultPwshAvailable();

        // Result must be a real bool (not throw). On non-Windows runners the
        // probe trivially returns true (it doesn't gate off-Windows anyway —
        // see the Factory_PowerShell_OnNonWindows_PwshProbe_DoesNotGate test).
        if (!OperatingSystem.IsWindows())
            result.ShouldBeTrue(
                customMessage: "DefaultPwshAvailable() MUST return true off-Windows so the auto-fallback branch never accidentally activates on Linux/macOS where Windows PowerShell doesn't exist.");
    }
}
