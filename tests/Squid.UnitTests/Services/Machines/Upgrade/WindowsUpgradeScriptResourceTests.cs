using System.IO;
using System.Linq;
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
            customMessage: "must construct .sha256 companion URL by appending '.sha256' to the download URL — same convention Linux uses + same convention the release workflows publish");
        content.ShouldContain("DownloadString",
            customMessage: "SHA companion must be fetched via WebClient.DownloadString, NOT Invoke-WebRequest. PS 5.1's Invoke-WebRequest returns .Content as byte[] for application/octet-stream companions (GitHub Releases' default Content-Type), so the whitespace split yields garbage, the 64-hex regex fails, and verification is silently skipped — letting a corrupt download through. DownloadString always returns a string.");
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

        // The catch block must exist (WebClient.DownloadString throws a
        // WebException on 404 — must be caught + treated as "no SHA, fall through").
        content.ShouldContain("catch",
            customMessage: "WebClient.DownloadString 404 / network failure on .sha256 fetch MUST be caught — air-gap mirrors without companion files would otherwise fail every upgrade with a transport error");
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

    [Fact]
    public void Resource_ZipDownload_UsesWebClient_ForcesTls12_NotInvokeWebRequestOutFile()
    {
        // Root cause of the field report (upgrade to 1.8.13 failed with
        // "End of central directory record could not be found", exit 14):
        // Windows PowerShell 5.1's `Invoke-WebRequest -OutFile` corrupts large
        // binary downloads on some hosts (truncated/garbled bytes), so the
        // downstream archive extraction can't find the central directory.
        // WebClient.DownloadFile streams the bytes verbatim. Verified on the
        // reporting operator's box: WebClient returned the byte-exact archive
        // (size + SHA match) where Invoke-WebRequest had returned a corrupt one.
        var content = LoadResource();

        content.ShouldContain("System.Net.WebClient",
            customMessage: "the zip archive MUST be downloaded via System.Net.WebClient — Invoke-WebRequest -OutFile corrupts large binaries on PS 5.1");
        content.ShouldContain("DownloadFile",
            customMessage: "WebClient.DownloadFile streams bytes verbatim, unlike Invoke-WebRequest -OutFile");
        content.ShouldContain("SecurityProtocolType]::Tls12",
            customMessage: "must force TLS 1.2 before the download — PS 5.1 may negotiate 1.0/1.1 by default, which GitHub rejects with a connection reset");

        // -OutFile (the Invoke-WebRequest binary-download pattern that corrupts
        // large archives) may appear in an explanatory comment, but must never
        // be INVOKED. Scan non-comment lines only. The health-check probe uses
        // Invoke-WebRequest legitimately but without -OutFile, so it's unaffected.
        var executableLines = string.Join("\n", content
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("#")));

        executableLines.ShouldNotContain("-OutFile",
            customMessage: "the binary archive download must NOT use Invoke-WebRequest -OutFile (the PS 5.1 large-binary corruption pattern that produced the 'End of central directory' failure). Use WebClient.DownloadFile.");
    }

    [Fact]
    public void Resource_Extraction_UsesZipFileExtractToDirectory_NotExpandArchive()
    {
        // Companion to the WebClient fix: extraction uses the BCL
        // [System.IO.Compression.ZipFile]::ExtractToDirectory rather than the
        // Expand-Archive cmdlet. The BCL API is more robust on large /
        // Linux-`zip`-built archives and surfaces a clearer error than the
        // cmdlet wrapper. Add-Type loads the assembly on PS 5.1.
        var content = LoadResource();

        content.ShouldContain("ZipFile]::ExtractToDirectory",
            customMessage: "extraction MUST use [System.IO.Compression.ZipFile]::ExtractToDirectory — more robust than Expand-Archive on large / cross-platform-built zips");

        // Expand-Archive may legitimately appear in an explanatory comment
        // ("...NOT Expand-Archive..."). What must NOT appear is an actual
        // invocation — scan non-comment lines only.
        var executableLines = string.Join("\n", content
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("#")));

        executableLines.ShouldNotContain("Expand-Archive",
            customMessage: "Expand-Archive must not be INVOKED — it was flaky on the large agent archive and produced the opaque 'End of central directory' failure. (An explanatory comment mentioning it is fine.)");
    }

    [Fact]
    public void Resource_ContainsNoNonAsciiCharacters_PreventsMojibakeInWebLog()
    {
        // The field report also showed mojibake in the web-surfaced upgrade log
        // ("ʹ�á�3�...", box-drawing turned to "â??"). Windows PowerShell 5.1
        // mis-decodes non-ASCII (em-dashes, arrows, box-drawing) under the OEM
        // codepage, and those bytes flow into the upgrade log the server renders.
        // The entire script must be pure ASCII so log lines stay legible without
        // RDP. Mirrors the WindowsPowerShellScriptBuilder em-dash drift detector.
        var content = LoadResource();

        var offenders = content
            .Select((ch, idx) => (ch, idx))
            .Where(t => t.ch > '\x7F')
            .Take(10)
            .Select(t => $"U+{(int)t.ch:X4} at offset {t.idx}")
            .ToList();

        offenders.ShouldBeEmpty(
            customMessage: "upgrade-windows-tentacle.ps1 must be pure ASCII so PS 5.1 + the web log don't mojibake it. " +
                           $"Non-ASCII found: {string.Join(", ", offenders)}. Replace em-dashes/arrows/box-drawing with ASCII (--, ->, -).");
    }

    [Fact]
    public void Resource_DownloadPath_HardenedForReliability_RetryProxyAndExtractSwap()
    {
        // Near-100% upgrade success requires surviving the common transient
        // Windows failures: a network blip on the download, an authenticated
        // corporate proxy, and a Defender file-lock on extract / swap. Pin each
        // hardening measure so a future edit can't silently drop it.
        var content = LoadResource();

        content.ShouldContain("function Invoke-WithRetry",
            customMessage: "must define a generic retry helper so a single transient failure doesn't fail the whole upgrade");
        content.ShouldContain("Invoke-WithRetry -Label 'archive download'",
            customMessage: "the binary download MUST be wrapped in retry — a single TCP reset / CDN 503 / proxy blip is the #1 field cause of upgrade failure");
        content.ShouldContain("UseDefaultCredentials",
            customMessage: "WebClient MUST use default credentials so authenticated corporate proxies (407) don't block the download");
        content.ShouldContain("DefaultNetworkCredentials",
            customMessage: "the proxy MUST be given default network credentials for NTLM/Kerberos corporate proxies");
        content.ShouldContain("Invoke-WithRetry -Label 'archive extraction'",
            customMessage: "extraction MUST retry — Defender can briefly lock a freshly-written file mid-extract");
        content.ShouldContain("Invoke-WithRetry -Label 'binary swap'",
            customMessage: "the Move-Item swap MUST retry — Defender can briefly lock the freshly-extracted binary");
    }

    [Fact]
    public void Resource_DownloadValidatedAsZip_RejectsNonZipContent()
    {
        // A non-zip download (proxy error page returned with HTTP 200, an HTML 404,
        // a 0-byte / truncated write) must fail with a clear message here, not an
        // opaque "central directory" error at extraction time. The check validates
        // the PK magic bytes (0x50 0x4B) rather than a size floor — a valid zip of
        // ANY size passes (a fixed floor would wrongly reject small archives, and
        // would miss large HTML error pages).
        var content = LoadResource();

        content.ShouldContain("0x50",
            customMessage: "must validate the download starts with the zip 'PK' magic byte 0x50");
        content.ShouldContain("0x4B",
            customMessage: "must validate the download's second byte is the zip 'PK' magic byte 0x4B — rejects HTML/JSON error pages and truncated downloads of any size");
    }

    [Fact]
    public void Resource_UpgradeLog_UsesPlainLanguage_NotPhaseAbJargon()
    {
        // Operator feedback: "Phase A / Phase B" jargon is opaque in the web log.
        // The raw upgrade log must describe what's happening in plain language.
        // (The structured event `phase` field stays 'A'/'B' as the data contract;
        // the web relabels it for display.)
        var content = LoadResource();

        content.ShouldContain("Preparing upgrade to",
            customMessage: "the download phase must log 'Preparing upgrade to <version>', not 'Phase A starting'");
        content.ShouldContain("Installing $TARGET_VERSION",
            customMessage: "the swap phase must log 'Installing <version>', not 'Phase B starting'");
        content.ShouldContain("Upgrade complete --",
            customMessage: "completion must log 'Upgrade complete', not 'Phase B complete'");
    }

    [Fact]
    public void Resource_ForcesEnglishCulture_AndSanitizesLog_PreventsExceptionMojibake()
    {
        // Exception messages on a non-Latin OS (e.g. Chinese Windows) are localized
        // and mojibake in the web-surfaced upgrade log. Force en-US .NET messages so
        // the text is English, AND ASCII-sanitize every log line as belt-and-braces.
        var content = LoadResource();

        content.ShouldContain("CurrentUICulture",
            customMessage: "must force the thread UI culture so .NET exception messages render in English, not the OS locale");
        content.ShouldContain("'en-US'",
            customMessage: "must pin en-US so exception text is plain ASCII English regardless of OS locale");
        content.ShouldMatch(@"\$safeLine\s*=\s*\$Line\s*-replace",
            customMessage: "Append-UpgradeLog must ASCII-sanitize each line so OS-localized exception text can't mojibake the web log");
    }
}
