using System.Globalization;
using System.Text;
using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Platform;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Platform;

/// <summary>
/// P1-Phase12.B.1 — pin the cross-platform process-launcher abstraction.
///
/// <para><b>Why this exists</b>: pre-Phase-12.B the agent had per-syntax
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
[Trait("Category", TentacleTestCategories.Core)]
public sealed class ProcessLauncherTests
{
    // ── BashProcessLauncher PSI shape — pinned bit-for-bit from pre-Phase-12 ───

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

        // The -File form is what the pre-Phase-12 launcher used.
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
        // Phase-12.B.1: PowerShell always routes to pwsh-Core. Phase-12.B.2
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

    // ── WindowsPowerShellProcessLauncher PSI shape (Phase 12.B.2) ──────────────
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
        // Phase-12.B.3 — preserve pre-Phase-12 behaviour: PowerShell on Windows
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
}
