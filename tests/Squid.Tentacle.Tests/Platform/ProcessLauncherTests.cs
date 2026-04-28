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
}
