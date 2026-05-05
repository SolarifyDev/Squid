using System.IO;
using System.Reflection;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// pins the embedded PowerShell upgrade-template resource
/// so a future "let's reorganise Resources/" refactor can't silently drop
/// the file from the assembly. Mirrors how the Linux side relies on
/// <c>upgrade-linux-tentacle.sh</c> being shipped as an embedded resource
/// resolved by manifest name in <c>LinuxTentacleUpgradeStrategy</c>.
///
/// <para>The  <c>WindowsTentacleUpgradeStrategy</c> will load
/// the template by manifest name, substitute placeholders (TARGET_VERSION,
/// DOWNLOAD_URL, etc.), inject the per-method snippet from the renderer
/// chain (zip / future MSI / future Chocolatey), and dispatch the result
/// over Halibut.  ships the template + ZipUpgradeMethod; Phase
/// 12.E.3 wires the loader.</para>
///
/// <para><b>Why pin placeholders explicitly</b>: a placeholder rename in
/// the .ps1 (e.g. <c>{{TARGET_VERSION}}</c> → <c>{{TARGETVERSION}}</c>)
/// without matching the strategy's substitution code would
/// produce silently-broken upgrades on the agent (the literal
/// <c>{{TARGET_VERSION}}</c> string ends up in the running script,
/// download URL is malformed, install fails with a confusing 404).
/// Pinned-by-test makes the drift compile/test-time visible.</para>
/// </summary>
public sealed class WindowsUpgradeScriptResourceTests
{
    /// <summary>
    /// Embedded resource name. Mirrors the
    /// <see cref="LinuxTentacleUpgradeStrategy"/> resource layout under
    /// <c>Squid.Core.Resources.Upgrade.upgrade-linux-tentacle.sh</c>.
    /// </summary>
    private const string ResourceName = "Squid.Core.Resources.Upgrade.upgrade-windows-tentacle.ps1";

    private static string LoadResource()
    {
        var asm = typeof(TentacleVersionRegistry).Assembly;

        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found in assembly '{asm.GetName().Name}'. " +
                $"Available resources: {string.Join(", ", asm.GetManifestResourceNames())}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Resource_IsEmbeddedInSquidCoreAssembly()
    {
        // Resource is registered via Squid.Core.csproj's
        // `<EmbeddedResource Include="Resources\Upgrade\*" />` glob — but
        // a future "let's narrow this glob to be more explicit" refactor
        // could accidentally drop the .ps1. This test makes that compile-time
        // / test-time visible.
        var asm = typeof(TentacleVersionRegistry).Assembly;
        var resources = asm.GetManifestResourceNames();

        resources.ShouldContain(ResourceName,
            customMessage: $"Available resources: {string.Join(", ", resources)}");
    }

    [Fact]
    public void Resource_HasNonTrivialContent()
    {
        // Defends against a "ship empty file" mistake (touch upgrade-windows-tentacle.ps1
        // without writing it) — the assembly would build, the manifest would
        // include the file, but the actual upgrade dispatch would fail
        // immediately at PowerShell parse time with a confusing "input is null"
        // error on the agent.
        var content = LoadResource();

        content.Length.ShouldBeGreaterThan(500,
            customMessage: "upgrade-windows-tentacle.ps1 must be non-trivial — Phase A (download/extract) " +
                           "+ Phase B (stop/swap/start) is at least a few hundred lines of PowerShell");
    }

    [Theory]
    [InlineData("{{TARGET_VERSION}}")]
    [InlineData("{{DOWNLOAD_URL}}")]
    [InlineData("{{EXPECTED_SHA256}}")]
    [InlineData("{{INSTALL_DIR}}")]
    [InlineData("{{SERVICE_NAME}}")]
    [InlineData("{{HEALTHCHECK_URL}}")]
    [InlineData("{{INSTALL_METHODS}}")]
    public void Resource_ContainsPlaceholder(string placeholder)
    {
        // The WindowsTentacleUpgradeStrategy will use string
        // replacement on these EXACT tokens. A drift on either side
        // (template renames a placeholder; strategy doesn't update its
        // substitution table; or vice-versa) produces a script that contains
        // literal "{{TARGET_VERSION}}" text on the agent — download URL is
        // malformed, install fails with a confusing 404 or PowerShell parse
        // error. Pin every placeholder explicitly here.
        var content = LoadResource();

        content.ShouldContain(placeholder,
            customMessage: $" strategy substitution depends on this token; renaming requires updating both sides.");
    }

    [Fact]
    public void Resource_ContainsArchDetection()
    {
        // Windows arch detection: $env:PROCESSOR_ARCHITECTURE → AMD64 / ARM64.
        // Mirrors the Linux side's `uname -m` arch detection. This test pins
        // that the .ps1 has SOME form of arch detection — the exact case-style
        // is template-author choice, but the env var name is the canonical
        // Windows API.
        var content = LoadResource();

        content.ShouldContain("PROCESSOR_ARCHITECTURE",
            customMessage: "PowerShell's canonical arch-detection env var on Windows; needed to pick win-x64 vs win-arm64 in the download URL");
    }

    [Fact]
    public void Resource_DocumentsExitCodeContract()
    {
        // The Linux template documents its exit codes in the header comment
        // so operators reading the script can map a failure to a specific
        // root cause. Same operator-readability discipline applies to the
        // Windows side. We don't pin specific codes (those evolve as the
        // template grows) but we pin that the documentation block exists.
        var content = LoadResource();

        content.ShouldContain("Exit code", Case.Insensitive,
            customMessage: "the .ps1 must document its exit codes in the header so operators see them at a glance");
    }

    // ========================================================================
    //  polish contracts (4 fixes added in 12.E.4 to close the
    // pre-real-dispatch fragility surfaced by the architectural audit).
    // Each pair is: (1) a positive Fact pinning the new behaviour, and where
    // applicable (2) a NEGATIVE Fact pinning that the prior buggy form is gone.
    // ========================================================================

    [Fact]
    public void Resource_GatesOnIdentityAtTopOfScript_AdminOrSystemRequired()
    {
        // the template assumes detach happened (Task Scheduler
        // /RU SYSTEM). If the wrapper failed silently and the
        // script ran as a regular user, Stop-Service / Move-Item would fail
        // mid-upgrade with confusing "Access is denied" errors at random
        // points. Failing fast at a specific identity check exits cleanly
        // with code 15 + a message naming the wrapper as the root cause.
        var content = LoadResource();

        content.ShouldContain("WindowsIdentity",
            customMessage: "must read current identity to gate Admin/SYSTEM at script top");
        content.ShouldContain("IsSystem",
            customMessage: "LocalSystem identity is the canonical Task-Scheduler /RU SYSTEM execution context");
        content.ShouldContain("WindowsBuiltInRole]::Administrator",
            customMessage: "Administrator role membership is the secondary acceptable identity (interactive ops upgrade)");
        content.ShouldContain("exit 15",
            customMessage: "must use the documented exit code (15 = insufficient privileges) so operators map back to the table in the header");
    }

    [Fact]
    public void Resource_DocumentsExitCode15_InsufficientPrivileges()
    {
        // Audit Rule 8: every operator-facing exit code must be in the header
        // documentation table.  adds 15; the table must list it.
        var content = LoadResource();

        content.ShouldContain("15",
            customMessage: "exit 15 must appear in the documented exit-code table");
        content.ShouldContain("insufficient privileges", Case.Insensitive,
            customMessage: "exit 15 must be documented as the privilege-gate exit");
    }

    [Fact]
    public void Resource_SkipsSha256VerificationWhenEmpty_DoesNotFalsifyEveryUpgrade()
    {
        // Audit (architectural review pre-12.E.4): the template
        // unconditionally compared $actualSha against $EXPECTED_SHA256.ToLower().
        // The strategy will (per Linux parity) substitute empty string until
        // the build pipeline publishes per-archive hashes — empty.ToLower()=""
        // and (Get-FileHash).Hash != "" → exit 7 EVERY upgrade. The fix is to
        // skip verification when EXPECTED_SHA256 is whitespace-only.
        //
        //  update: empty-skip is now BEHIND the
        // opportunistic-fetch attempt. The template still skips verification
        // when nothing populates EXPECTED_SHA256 (neither strategy substitution
        // NOR the .sha256 companion fetch succeed) — backward compat for older
        // releases without companion files. Pin both: the IsNullOrWhiteSpace
        // gate enters the opportunistic-fetch path, AND there's a
        // "skipping verification" log line on the fall-through path.
        var content = LoadResource();

        content.ShouldContain("IsNullOrWhiteSpace($EXPECTED_SHA256)",
            customMessage: "must gate on empty placeholder (mirrors Linux EXPECTED_SHA256 empty-handling)");
        content.ShouldContain("skipping verification",
            customMessage: "operator must see the skip decision in the upgrade log so they understand why no hash check ran");
    }

    [Fact]
    public void Resource_OpportunisticSha256Fetch_MirrorsLinuxPattern()
    {
        // Windows .ps1 must mirror upgrade-linux-tentacle.sh's
        // opportunistic .sha256 companion fetch (Linux pattern at sh:418-429).
        // Without this, releases shipped per  (which publishes
        // .sha256 companion files) would NOT activate verification on Windows
        // — only Linux.  closes that asymmetry.
        var content = LoadResource();

        content.ShouldContain("$DOWNLOAD_URL.sha256",
            customMessage: "must construct .sha256 companion URL by appending '.sha256' to the download URL — same convention Linux uses + same convention 's release workflows publish");
        content.ShouldContain("Invoke-WebRequest",
            customMessage: "must use PowerShell's HTTP client (no external curl dep on Windows)");
        content.ShouldContain("'^[0-9a-f]{64}$'",
            customMessage: "must validate the fetched content is exactly 64 hex chars — guards against HTML 404 pages, partial bytes, etc. masquerading as a SHA");
    }

    [Fact]
    public void Resource_OpportunisticSha256Fetch_HandlesAirgapMirrorsWithoutCompanion()
    {
        // Pin the backward-compat path: when the .sha256 companion is absent
        // (older releases or air-gap mirrors that haven't replicated the
        // companion files yet), the opportunistic fetch fails NON-FATALLY
        // and the script falls through to "skipping verification". Mirrors
        // Linux's "::info::" non-error log line.
        var content = LoadResource();

        // The catch block must exist (Invoke-WebRequest with -ErrorAction Stop
        // throws on 404 — must be caught + treated as "no SHA, fall through").
        content.ShouldContain("catch",
            customMessage: "Invoke-WebRequest 404 / network failure on .sha256 fetch MUST be caught — air-gap mirrors without companion files would otherwise fail every upgrade with a transport error");
        content.ShouldContain("No .sha256 companion at",
            customMessage: "fall-through log line must explain the absence — operators investigating a 'why didn't SHA verify' question see the absent-companion reason");
    }

    [Fact]
    public void Resource_OpportunisticSha256Fetch_StripsToFirstWhitespaceToken()
    {
        // sha256sum's default output is `<64-hex>  <filename>`. The agent
        // must take ONLY the first whitespace-delimited token and strip
        // trailing space — pinning this prevents a future "let's just
        // ToLower the whole line" mistake that would treat the filename
        // suffix as part of the hash.
        var content = LoadResource();

        content.ShouldContain("-split '\\s+'",
            customMessage: "must split on whitespace to isolate the hex digest from the filename suffix that sha256sum's default format includes");
    }

    [Fact]
    public void Resource_BakDirComputedViaSplitPath_NotStringConcatenation()
    {
        // Audit (architectural review pre-12.E.4): "$INSTALL_DIR.bak" string
        // interpolation would produce ".bak" inside the install dir if the
        // operator's INSTALL_DIR has a trailing backslash ("C:\Squid\" →
        // "C:\Squid\.bak" = a hidden directory inside Squid, not a sibling).
        // Split-Path -Parent + Split-Path -Leaf normalises away trailing
        // separators so $bakDir is always a true sibling path.
        var content = LoadResource();

        content.ShouldContain("Split-Path -Parent $INSTALL_DIR",
            customMessage: "must split out the parent directory to construct .bak as a true sibling, not a hidden child");
        content.ShouldContain("Split-Path -Leaf $INSTALL_DIR",
            customMessage: "must split the leaf so .bak is appended to a clean directory name without trailing separators");
        content.ShouldContain("Join-Path $installParent",
            customMessage: "must use Join-Path on the canonical sibling-dir construction so spaces and separators are handled correctly by cmdlet binding");
    }

    [Fact]
    public void Resource_DoesNotConstructBakViaStringInterpolation_PinsRegression()
    {
        // Reverse-verify the polish above: confirm the buggy "$INSTALL_DIR.bak"
        // string interpolation form is GONE. Without this negative pin, a
        // future "let's simplify" refactor could inline the construction back
        // and trigger the trailing-slash bug at a random operator's site.
        var content = LoadResource();

        content.ShouldNotContain("\"$INSTALL_DIR.bak\"",
            customMessage: "the buggy string-interpolation form must not return — pinning Split-Path approach as the only way bakDir is constructed");
    }

    [Fact]
    public void Resource_PhaseB_UsesExtractDirVariable_NotGetChildItemSearch()
    {
        // Audit (architectural review pre-12.E.4): the template
        // searched for the staging dir via `Get-ChildItem | Sort-Object
        // LastWriteTimeUtc -Descending | Select -First 1`. This races with
        // any earlier abandoned staging dir (operator dispatched twice, the
        // first attempt left a directory in %TEMP%). The host-scoped lock at
        // ~line 145 prevents true concurrency, but the search added unnecessary
        // ambiguity. Phase A already binds $extractDir and the variable is
        // in scope at Phase B; just use it.
        var content = LoadResource();

        content.ShouldNotContain("Sort-Object LastWriteTimeUtc -Descending",
            customMessage: "Get-ChildItem | LastWriteTime racy search must be gone; Phase B reads $extractDir directly");
        content.ShouldContain("if (-not (Test-Path $extractDir))",
            customMessage: "Phase B must validate $extractDir exists (defense against Phase A leaving the variable empty / temp dir cleanup) and exit cleanly with a clear staging-disappeared message");
    }
}
