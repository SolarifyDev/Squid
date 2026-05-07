using System.Diagnostics;
using System.Text;
using Squid.Core.Services.Machines.Upgrade;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.L.E.1 — baseline E2E coverage for
/// <c>upgrade-linux-tentacle.sh</c>'s placeholder substitution + bash
/// parse-cleanliness contract.
///
/// <para><b>Tier</b>: 🟢 High-fidelity. The production .sh template is
/// loaded from disk verbatim (the same bytes the strategy embeds); the
/// strategy's <c>RenderInnerScript</c> is invoked to produce the rendered
/// .sh; the rendered output is parsed by REAL bash via <c>bash -n</c>
/// (parse-only mode — no execution). Catches placeholder-substitution
/// regressions that would only surface at agent-side runtime.</para>
///
/// <para><b>Why this matters</b>: J.E.3 first-attempt on Windows caught a
/// production P0 where `{{INSTALL_METHODS}}` in a comment got rewritten
/// by `String.Replace`, splicing multi-line PowerShell into a #-prefixed
/// line and producing parse errors invisible to ALL prior tests (no
/// suite ran the rendered .ps1 through a real parser). The Linux .sh
/// has a similar substitution shape; this test pins the parse contract
/// so the same bug class can't slip through silently on the Linux side.
/// Pairs with <see cref="LinuxScriptParsesAsBash"/>.</para>
///
/// <para><b>Cross-platform</b>: bash + grep are universally available on
/// Linux + macOS. Tests skip-guard <c>OperatingSystem.IsLinux()</c> on
/// macOS dev hosts (matches the convention for Windows-only tests in
/// the sibling project).</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.UpgradeScript)]
public sealed class UpgradeLinuxScriptE2ETests
{
    // ========================================================================
    // Drift detector — placeholder set
    //
    // Pins which `{{TOKEN}}` placeholders the production .sh expects. The
    // strategy MUST substitute exactly this set; a future polish that
    // renames or adds a placeholder without updating the strategy would
    // ship an unsubstituted `{{NEW_PLACEHOLDER}}` literal — bash treats
    // that as a parameter expansion at parse time and may fail / silently
    // expand to empty.
    //
    // Cross-platform — runs on every dev host without a Linux guard.
    // ========================================================================

    [Fact]
    public void LinuxScript_PlaceholderSet_PinnedToProductionContract()
    {
        var template = File.ReadAllText(LocateLinuxTemplate());

        // Same regex shape as the Windows pin. Includes digits because a
        // future placeholder might have numeric suffix (e.g. SHA256).
        var matches = Regex.Matches(template, @"\{\{([A-Z0-9_]+)\}\}");
        var foundPlaceholders = matches.Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s).ToArray();

        // Linux-specific shape. Note SERVICE_USER (Linux runs services
        // under a dedicated user, Windows uses SYSTEM identity).
        var expected = new[]
        {
            "DOWNLOAD_URL",
            "EXPECTED_SHA256",
            "HEALTHCHECK_URL",
            "INSTALL_DIR",
            "INSTALL_METHODS",
            "SERVICE_NAME",
            "SERVICE_USER",
            "TARGET_VERSION"
        };

        foundPlaceholders.ShouldBe(expected, ignoreOrder: false,
            customMessage: $"production upgrade-linux-tentacle.sh placeholder set drifted from strategy substitution map. " +
                          $"Expected: [{string.Join(", ", expected)}]. " +
                          $"Found: [{string.Join(", ", foundPlaceholders)}]. " +
                          $"FIX: extend LinuxTentacleUpgradeStrategy.RenderInnerScript to substitute the new placeholder " +
                          $"(or remove it from the .sh if production removed it). Without this fix, the rendered .sh will " +
                          $"contain a literal `{{{{NEW_PLACEHOLDER}}}}` token that bash treats as parameter expansion — " +
                          $"either failing parse (`bad substitution`) or silently expanding to empty (worse: hides the bug).");
    }

    // ========================================================================
    // Drift detector #2 — placeholder uniqueness
    //
    // Same bug class as the J.E.3.1 Windows P0 fix: any `{{TOKEN}}`
    // appearing more than once in the template gets rewritten everywhere
    // by `String.Replace`. For multi-line substitutions (e.g.
    // INSTALL_METHODS → if-block), this splices code into single-line
    // contexts (comments / string interpolations) producing silent
    // syntax errors at agent runtime.
    //
    // The Linux .sh CURRENTLY has every placeholder appearing exactly
    // once. This test pins that — a future polish that mentions a
    // placeholder name in a comment or doc-string accidentally gets
    // caught here at build time, not at the next operator-broken-upgrade
    // incident.
    // ========================================================================

    [Fact]
    public void LinuxScript_PlaceholderTokens_AppearExactlyOnceInTemplate()
    {
        var template = File.ReadAllText(LocateLinuxTemplate());

        var matches = Regex.Matches(template, @"\{\{([A-Z0-9_]+)\}\}");
        var occurrences = matches
            .Cast<Match>()
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (name, count) in occurrences)
        {
            count.ShouldBe(1,
                customMessage: $"placeholder '{{{{{name}}}}}' appears {count} times in upgrade-linux-tentacle.sh — MUST be exactly once. " +
                              $"String.Replace substitutes EVERY occurrence, so a comment-line mention of the placeholder name gets rewritten too. " +
                              $"For multi-line substitutions (e.g. INSTALL_METHODS → multi-statement block), this splices bash code into a `#`-prefixed " +
                              $"comment line — bash parses the FIRST line as comment but treats subsequent lines as orphan code, producing parse errors " +
                              $"that ONLY surface at agent-side `systemd-run --scope` re-exec. The pre-scope phase exit code stays 0 (its work was done " +
                              $"— the scope was registered), strategy maps to Initiated, but the agent's scope-phase parse-fails and last-upgrade.json " +
                              $"is never written. Identical bug class as the J.E.3.1 Windows {{{{INSTALL_METHODS}}}} comment fix. " +
                              $"FIX: edit upgrade-linux-tentacle.sh to remove the duplicate occurrence (typically a comment-line mention of the placeholder by name).");
        }
    }

    // ========================================================================
    // Drift detector #3 — bash parses the rendered .sh cleanly
    //
    // The strategy's full RenderInnerScript output MUST be parseable by
    // bash. Catches the same bug class the Windows pin catches but at
    // a different layer: substring-checks (placeholder-appears-once) +
    // syntactic check (bash -n exits 0). Belt-and-braces — if a future
    // polish slips a placeholder past the substring check (e.g. a new
    // multi-line substitution that splices into a different odd context),
    // bash parsing surfaces the actual error.
    //
    // Skip-on-non-bash — runs on Linux + macOS (both ship bash). Windows
    // dev hosts skip cleanly via the operating-system guard.
    // ========================================================================

    [Fact]
    public async Task LinuxScript_RenderedFromStrategy_ParsesCleanlyAsBash()
    {
        // bash -n is universally available on Linux + macOS. The Windows
        // sibling project skips this test (no native bash on windows-latest
        // runner image's PATH by default; WSL is opt-in).
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        // Render via the production strategy (NOT a test re-implementation).
        // If the strategy's substitution logic regresses, this test catches it.
        // Note: Linux strategy exposes `BuildScript` (Windows uses
        // `RenderInnerScript`); they're analogous APIs at different layers
        // (Linux .sh self-contains the scope re-exec, so there's no separate
        // outer wrapper like Windows's Task Scheduler dispatch wrapper).
        var rendered = LinuxTentacleUpgradeStrategy.BuildScript("1.6.0", LinuxTentacleUpgradeStrategy.DefaultMethodOrder);

        // Sanity assertion before invoking bash — fail fast with a clear
        // message if the strategy's substitution dropped a placeholder.
        rendered.ShouldNotMatch(@"\{\{[A-Z0-9_]+\}\}",
            customMessage: "rendered .sh STILL contains an unsubstituted placeholder. " +
                          "RenderInnerScript missed a placeholder — bash will treat it as parameter expansion at runtime. " +
                          "FIX: add the missing .Replace call.");

        // Write rendered to a temp file + invoke `bash -n` (parse-only).
        // Temp file path uses /tmp on Linux/macOS — universally writable.
        var tempScript = Path.Combine(Path.GetTempPath(), $"squid-linux-script-parse-check-{Guid.NewGuid():N}.sh");
        File.WriteAllText(tempScript, rendered);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-n \"{tempScript}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to launch bash for parse check");

            var stderr = await proc.StandardError.ReadToEndAsync();
            var stdout = await proc.StandardOutput.ReadToEndAsync();

            proc.WaitForExit(5_000).ShouldBeTrue("bash -n must complete within 5s for a script of this size");

            proc.ExitCode.ShouldBe(0,
                customMessage: $"bash -n rejected the rendered upgrade-linux-tentacle.sh — placeholder substitution produced syntactically-invalid bash. " +
                              $"This is the same bug class as the J.E.3.1 Windows {{{{INSTALL_METHODS}}}} comment fix: a multi-line substitution spliced code into " +
                              $"an unexpected context (comment / string / heredoc) and broke the parse. " +
                              $"bash stderr:\n{stderr}\n" +
                              $"bash stdout (usually empty for -n):\n{stdout}");
        }
        finally
        {
            try { File.Delete(tempScript); } catch { /* best-effort */ }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string LocateLinuxTemplate()
    {
        // Walk up from the test assembly's bin directory to the repo root.
        // Same shape as Windows project's LocateProductionTemplate but
        // pointing at the .sh instead of .ps1.
        var thisAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var dir = thisAssemblyDir;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "src", "Squid.Core", "Resources", "Upgrade", "upgrade-linux-tentacle.sh");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("Could not locate upgrade-linux-tentacle.sh from the test assembly's directory tree");
    }
}
