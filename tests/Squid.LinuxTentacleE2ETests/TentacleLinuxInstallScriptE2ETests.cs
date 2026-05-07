using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.M.L.A.1 — first E2E coverage of the production
/// <c>deploy/scripts/install-tentacle.sh</c> bootstrap installer.
///
/// <para>This is the smallest viable scope for Section A: <b>bogus
/// version → exit 1</b>. No successful install means no symlinks, no
/// service install, no sudoers — minimal pollution surface, minimal
/// cleanup, but real coverage of the .sh's argument parsing + URL
/// construction + curl error handling code paths.</para>
///
/// <para>Subsequent phases (12.M.L.A.2+) will exercise the full install
/// happy path with comprehensive cleanup of <c>/etc/squid-tentacle</c>,
/// <c>/var/lib/squid-tentacle</c>, <c>/usr/local/bin/squid-tentacle</c>,
/// systemd unit, sudoers rule, etc. The fixture's <see cref="LinuxInstallScriptContext.Dispose"/>
/// already cleans all of those paths defensively, so this test class's
/// scope expansion is just additive.</para>
///
/// <para>Tier: 🟢 H (Rule 12.4) — drives the real production .sh against
/// real bash + real curl + real <see cref="Infrastructure.LocalReleaseMirror"/>
/// returning real HTTP 404. No mocks at OS-resource layer.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.InstallScript)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxInstallScriptE2ETests
{
    // ========================================================================
    // A2.u1-Linux — Bogus version → exit 1, clean error message, no partial install
    //
    // Production scenario this pins: operator typos `--version 1.0.0` as
    // `--version 1.O.O` (letter O instead of zero) OR specifies a version
    // that doesn't exist (typo'd commit-derived version). The .sh's
    // tarball download fails for both URL forms (plain + v-prefixed) →
    // exits 1 with operator-actionable error.
    //
    // Why this test catches a real regression class:
    //   - .sh's --version arg parsing fails to set VERSION → falls back
    //     to "latest" → APT install path attempts (different code path
    //     entirely) → silent success on the wrong version
    //   - .sh's URL construction loses --version value → downloads
    //     "latest" tarball regardless of operator's intent
    //   - .sh's curl error handling regression → swallows 404 + reports
    //     success → operator believes wrong version installed
    //   - .sh's exit-on-failure regression → continues past failed
    //     download → tries `tar xzf` on empty file → cryptic error
    //
    // Test mechanism: configure the LocalReleaseMirror to 404 BOTH the
    // plain-version AND the v-prefixed download URLs (.sh tries both per
    // line 234-241). No tarball is staged → both URL forms 404. .sh's
    // download_ok wrapper retries 3× per URL; both URLs fail → exit 1
    // with "Could not download" error.
    //
    // Reverse-asserts: INSTALL_DIR was never created (.sh's mkdir -p
    // happens AFTER successful curl download); /etc/* paths were never
    // touched; no service installed.
    // ========================================================================

    [Fact]
    public void A2u1_BogusVersion_ExitsOneWithCleanErrorAndNoPartialInstall()
    {
        if (!LinuxInstallScriptContext.IsAvailable) return;

        using var ctx = new LinuxInstallScriptContext();

        // Configure mirror to 404 every download URL for this version.
        // The .sh tries plain + v-prefixed, so both must 404.
        const string bogusVersion = "999.999.999-bogus-test";
        ctx.Mirror.ConfigureNotFoundForVersion(bogusVersion);
        ctx.Mirror.ConfigureNotFoundForVersion($"v{bogusVersion}");

        var (exitCode, output) = ctx.RunInstallScript(bogusVersion);

        // Exit 1 — install-tentacle.sh's documented "could not download"
        // failure code (line 240).
        exitCode.ShouldBe(1,
            customMessage: $"bogus --version MUST cause install-tentacle.sh to exit 1 after both download attempts (plain + v-prefixed) 404. " +
                          $"Got exit {exitCode}. " +
                          $"If 0: download somehow succeeded — mirror staged a tarball it shouldn't have, OR .sh's exit-on-failure regressed. " +
                          $"If non-1: a different .sh failure path fired BEFORE the curl loop (arch detect? root check?) — investigate. " +
                          $"output tail (last 2k chars):\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // Operator-actionable error MUST surface in stdout. Without this,
        // exit 1 + no message = operator has no idea what to fix.
        output.ShouldContain("Could not download",
            customMessage: $"stdout MUST contain operator-actionable 'Could not download' diagnostic. " +
                          $"output tail:\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // The error MUST echo BOTH attempted URLs (plain + v-prefixed).
        // Operators see them in logs to verify the mirror config and DNS
        // resolution (4xx vs DNS-fail are distinct fixes).
        output.ShouldContain($"{bogusVersion}/squid-tentacle-{bogusVersion}-",
            customMessage: $"stdout MUST echo the constructed download URL containing the version. Operators use this to verify their --version typo. " +
                          $"output tail:\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // Reverse-assert: INSTALL_DIR was never created. The .sh's
        // `mkdir -p "$INSTALL_DIR"` (line 251) only runs AFTER successful
        // curl download. If it exists, .sh's exit-on-failure regressed
        // and post-download steps ran on an empty/missing tarball.
        Directory.Exists(ctx.InstallDir).ShouldBeFalse(
            customMessage: $"INSTALL_DIR at {ctx.InstallDir} MUST NOT exist after a failed-download install. " +
                          $".sh's `mkdir -p` runs only after successful curl; if dir exists, the .sh proceeded past the download failure (regression).");

        // Reverse-assert: no system-wide artefacts. Belt-and-braces: the
        // .sh's post-extract block writes /etc/squid-tentacle/ etc. only
        // after a successful tarball download. If any of these got
        // written despite exit 1, the .sh's exit ordering regressed.
        Directory.Exists("/etc/squid-tentacle").ShouldBeFalse(
            customMessage: "/etc/squid-tentacle MUST NOT exist after a failed install. If present: .sh proceeded past the download failure into post-install steps (regression in exit ordering).");

        Directory.Exists("/var/lib/squid-tentacle").ShouldBeFalse(
            customMessage: "/var/lib/squid-tentacle MUST NOT exist after a failed install (created by the .sh's post-install block, which never ran).");

        File.Exists("/usr/local/bin/squid-tentacle").ShouldBeFalse(
            customMessage: "/usr/local/bin/squid-tentacle symlink MUST NOT exist after a failed install — symlink creation is post-extract.");

        ctx.MarkClean();
    }

    // ========================================================================
    // A1.h-Linux — Happy path: --version X.Y.Z installs binary + symlink chain
    //               + post-install dirs, exits 0
    //
    // Production scenario this pins: operator runs the documented one-liner
    //   curl -fsSL .../install-tentacle.sh | sudo bash -s -- --version 1.6.0
    // → binary lands at INSTALL_DIR, symlink chain on PATH, post-install
    // dirs (config, state, workspace) created, "Verified: squid-tentacle
    // executable" printed.
    //
    // Why this test is critical: it's THE prerequisite for every
    // downstream operation. If install regresses, no upgrade can run,
    // no register can persist, no deploy can dispatch. Currently zero
    // Linux E2E coverage on this path.
    //
    // Test mechanism:
    //   1. Build a tarball with a placeholder Squid.Tentacle binary
    //      (the test service script, which handles `version` + `help`
    //      subcommands and would block on systemd start otherwise).
    //   2. Stage on LocalReleaseMirror.
    //   3. Run install-tentacle.sh via sudo bash with --version (matches
    //      operator one-liner shape).
    //   4. Assert exit 0 + binary file exists + symlink chain wired up
    //      + post-install dirs (created).
    //
    // Why placeholder binary handles `help`: install-tentacle.sh's line
    // 510-516 verification step runs `squid-tentacle help`. Without a
    // `help` fast-path, the placeholder's main loop is sleep-forever,
    // and the install .sh would hang. Real Squid.Tentacle's CLI handles
    // help similarly — the J.M.L.A.2 first-runner caught this gap.
    //
    // Recommended overrides (set by fixture):
    //   - INSTALL_DIR=test-private  (avoids /opt collision)
    //   - DOWNLOAD_BASE=mirror      (offline + deterministic tarball)
    //   - NO_PKG_INSTALL=1          (skip APT/RPM probe — test is offline)
    //   - CREATE_USER=no            (skip useradd; sudoers block also skipped)
    //   - SQUID_BASE_URL=http://localhost:1  (defensive)
    //
    // Tier: 🟢 H (Rule 12.4) — drives the real production .sh against
    // real bash + real curl + real LocalReleaseMirror serving real
    // tar.gz + real sudo orchestration of mkdir + chmod + symlinks.
    //
    // Expected runtime: ~10-15s (curl LAN download + extract + chmod +
    // symlinks + dir creation + verify-help on placeholder).
    // ========================================================================

    [Fact]
    public void A1h_HappyPath_InstallsBinaryAndCreatesSymlinkChain()
    {
        if (!LinuxInstallScriptContext.IsAvailable) return;

        using var ctx = new LinuxInstallScriptContext();

        // Stage a tarball with the placeholder Squid.Tentacle binary.
        // The .sh tries plain version URL first (line 230); we serve
        // the same bytes for any URL via StagePreBuiltArchive.
        const string version = "1.6.0-installtest";
        var tarball = ctx.BuildInstallTarGz(version);
        ctx.Mirror.StagePreBuiltArchive(tarball);

        var (exitCode, output) = ctx.RunInstallScript(version);

        exitCode.ShouldBe(0,
            customMessage: $"happy-path install MUST exit 0. Got {exitCode}. " +
                          $"If 1: download/extract/symlink failed (likely curl URL mismatch with mirror, OR `tar xzf` rejected our tarball format, OR sudoer chain broke). " +
                          $"If 2: `Unknown option:` from arg parsing. " +
                          $"output tail (last 2k chars):\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // Operator-visible "Installation Complete" banner — pins the
        // .sh's success message contract.
        output.ShouldContain("Installation Complete",
            customMessage: $"stdout MUST contain 'Installation Complete' banner. Operators tail this for confirmation. output tail:\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // Verification step ran AND succeeded — the .sh prints
        // "Verified: squid-tentacle executable" only when `${BINARY_NAME}
        // help` exits 0. If absent: either help fast-path missing OR
        // PATH wiring broke.
        output.ShouldContain("Verified: squid-tentacle executable",
            customMessage: $"stdout MUST contain 'Verified: squid-tentacle executable' — the .sh's post-install verification (line 510-516) ran the binary's `help` subcommand successfully. " +
                          $"If absent: placeholder binary doesn't handle `help` (fall-through to sleep loop, .sh hangs OR exits non-zero on its own timeout) " +
                          $"OR symlink chain didn't put `squid-tentacle` on PATH. " +
                          $"output tail:\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // Binary exists at INSTALL_DIR/Squid.Tentacle (extracted by tar).
        var binaryPath = Path.Combine(ctx.InstallDir, "Squid.Tentacle");
        File.Exists(binaryPath).ShouldBeTrue(
            customMessage: $"Squid.Tentacle binary MUST exist at {binaryPath} after install. " +
                          "If absent: tar extraction failed silently, OR tarball had a different entry name (mirror serving wrong content?), OR INSTALL_DIR override didn't propagate to .sh.");

        // Symlink within INSTALL_DIR — `ln -sf $INSTALL_DIR/Squid.Tentacle $INSTALL_DIR/squid-tentacle`.
        var binaryNameSymlink = Path.Combine(ctx.InstallDir, "squid-tentacle");
        File.Exists(binaryNameSymlink).ShouldBeTrue(
            customMessage: $"well-known-name symlink MUST exist at {binaryNameSymlink}. " +
                          ".sh's `ln -sf $INSTALL_DIR/Squid.Tentacle $INSTALL_DIR/squid-tentacle` (line 264) ran but produced no symlink — defensive ln failed silently?");

        // Symlink on PATH (/usr/local/bin/) — `ln -sf $INSTALL_DIR/squid-tentacle /usr/local/bin/squid-tentacle`.
        File.Exists("/usr/local/bin/squid-tentacle").ShouldBeTrue(
            customMessage: "/usr/local/bin/squid-tentacle symlink MUST exist after install — operators register + run via this PATH-resolved name. " +
                          "If absent: .sh skipped the symlink (perhaps /usr/local/bin doesn't exist, but it's standard on every distro), OR sudo couldn't write there.");

        // Post-install dirs created (the .sh creates these unconditionally).
        Directory.Exists("/etc/squid-tentacle").ShouldBeTrue(
            customMessage: "/etc/squid-tentacle MUST exist after install (CONFIG_DIR — register + run persist instance state here). " +
                          "If absent: .sh's `mkdir -p $CONFIG_DIR` (line 296-299) didn't run — likely `[ ! -d $CONFIG_DIR ]` returned false (so dir already existed pre-test, but cleanup matrix should have removed it).");

        Directory.Exists("/var/lib/squid-tentacle").ShouldBeTrue(
            customMessage: "/var/lib/squid-tentacle MUST exist after install (STATE_DIR — upgrade flow's last-upgrade.json + lock + events go here).");

        ctx.MarkClean();
    }

    // ========================================================================
    // A2.u2-Linux — Plain --version 1.6.0 URL 404s, .sh falls back to v1.6.0
    //               and installs successfully
    //
    // Production scenario this pins: install-tentacle.sh tries TWO download
    // URL forms in sequence (line 230-241):
    //
    //   URL_PLAIN     = ${DOWNLOAD_BASE}/download/${VERSION}/squid-tentacle-${VERSION}-${RID}.tar.gz
    //   URL_V_PREFIXED = ${DOWNLOAD_BASE}/download/v${VERSION}/squid-tentacle-${VERSION}-${RID}.tar.gz
    //
    // Real-world driver: GitHub Releases tag conventions vary across
    // projects. Squid's current pipeline publishes both `v1.6.0` and
    // `1.6.0` tags, but operators on legacy / private mirrors might
    // only have ONE form. The .sh's retry-with-v-prefix is the
    // operator-friendly compatibility shim.
    //
    // Without this test, a regression in the retry logic ships silently:
    //   - .sh stops retrying on 404 → operator on legacy mirror sees
    //     install fail with "could not download 1.6.0" even though
    //     v1.6.0 IS published
    //   - .sh doesn't echo the retry attempt → operators have no log
    //     signal that the fallback was tried (vs "single shot fail")
    //   - .sh's exit-on-failure regression after retry → silently
    //     swallows the v-prefix 404 and exits 0 with no install
    //
    // Test mechanism: configure mirror to 404 ONLY the plain-version
    // URL path (via the new ConfigureNotFoundForPath surgical-404 API
    // — substring match with leading slash boundary). Mirror serves the
    // v-prefixed path normally via StagePreBuiltArchive. Run install,
    // assert exit 0 + retry log message + binary installed.
    //
    // Why surgical-404: ConfigureNotFoundForVersion("1.6.0") would also
    // 404 the v-prefixed URL (substring "1.6.0" appears in both paths).
    // ConfigureNotFoundForPath("/download/1.6.0/") matches ONLY the plain
    // form because the v-prefixed path contains "/download/v1.6.0/".
    //
    // Tier: 🟢 H. Reuses J.M.L.A.2's fixture + tarball builder; only
    // adds the surgical mirror config + retry-message assertion.
    //
    // Expected runtime: ~3-5s (curl 404 fast on plain URL → retry attempt
    // → tarball download succeeds via v-prefix path → standard install).
    // ========================================================================

    [Fact]
    public void A2u2_PlainVersion404FallsBackToVPrefix_InstallSucceeds()
    {
        if (!LinuxInstallScriptContext.IsAvailable) return;

        using var ctx = new LinuxInstallScriptContext();

        const string version = "1.6.0-vfallback";

        // Surgical 404: plain version URL only. v-prefixed URL still serves.
        // The leading slash in "/download/<version>/" disambiguates from
        // the v-prefixed sibling "/download/v<version>/" — ConfigureNotFoundForPath
        // does substring match, and `/download/v1.6.0-vfallback/` does NOT
        // contain `/download/1.6.0-vfallback/` (the leading char of the
        // version segment differs).
        ctx.Mirror.ConfigureNotFoundForPath($"/download/{version}/");

        // Stage tarball. Served by mirror at any URL that doesn't match
        // the surgical-404 path → v-prefixed URL serves successfully.
        var tarball = ctx.BuildInstallTarGz(version);
        ctx.Mirror.StagePreBuiltArchive(tarball);

        var (exitCode, output) = ctx.RunInstallScript(version);

        exitCode.ShouldBe(0,
            customMessage: $"v-prefix fallback install MUST succeed (plain URL 404, v-prefix serves). Got exit {exitCode}. " +
                          $"If 1: .sh's retry logic regressed — operator log should show 'retrying with v...' and install should still succeed. " +
                          $"If 2: arg parsing rejected --version. " +
                          $"output tail (last 2k chars):\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // Operator-visible signal: the .sh MUST log the retry attempt.
        // Without this, operators on legacy mirrors can't distinguish
        // "first attempt succeeded" from "fallback succeeded" — important
        // because if BOTH attempts failed they'd want to know which URL
        // forms were tried.
        output.ShouldContain($"retrying with 'v{version}'",
            customMessage: $"stdout MUST contain 'retrying with v{version}' so operators see the .sh tried both URL forms. " +
                          $"If absent: retry log line at .sh line 236 was dropped — operators tailing the log can't tell whether the fallback fired. " +
                          $"output tail:\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // Operator-visible: the v-prefixed URL appears in the second
        // download-attempt log message. If absent: the .sh constructed
        // the wrong URL form and the test would fail above with exit 1
        // anyway — but echoing the URL gives diagnostic precision.
        output.ShouldContain($"v{version}/squid-tentacle-{version}-",
            customMessage: $"stdout MUST echo the v-prefixed download URL containing 'v{version}/squid-tentacle-{version}-'. " +
                          "Operators verify which URL form succeeded by reading this log.");

        // Sanity: the install actually completed end-to-end via the v-prefix path.
        File.Exists(Path.Combine(ctx.InstallDir, "Squid.Tentacle")).ShouldBeTrue(
            customMessage: $"Squid.Tentacle binary MUST exist at {ctx.InstallDir}/Squid.Tentacle after v-prefix fallback install. " +
                          "If absent: .sh logged retry but never actually downloaded — retry construction may produce a malformed URL.");

        File.Exists("/usr/local/bin/squid-tentacle").ShouldBeTrue(
            customMessage: "/usr/local/bin/squid-tentacle symlink MUST exist after v-prefix fallback install — confirms post-extract steps ran the same way as the plain-URL happy path.");

        ctx.MarkClean();
    }

    // ========================================================================
    // A8.h-Linux — Re-running the installer is idempotent (must not fail
    //               on existing state)
    //
    // Production scenarios this pins:
    //   - Operator runs install-tentacle.sh twice to "refresh" or after a
    //     transient network failure
    //   - Fleet automation re-runs the installer on every machine boot
    //     (common pattern: cloud-init / Ansible / Salt invokes the
    //     installer; subsequent boots re-invoke for self-healing)
    //   - Operator pins a specific version, then re-runs to verify
    //     installation correctness
    //
    // Without this pin, regressions in idempotency ship silently:
    //   - Future polish adds an "already installed, error" check → fleet
    //     automation breaks on every machine after the first boot
    //   - tar xzf fails with EEXIST on a non-empty INSTALL_DIR → re-runs
    //     to repair partial installs become impossible
    //   - ln -sf regression → second run leaves stale symlink pointing
    //     to gone INSTALL_DIR
    //   - mkdir -p regression (someone changes to plain mkdir) → second
    //     run errors on existing /etc/squid-tentacle dir
    //
    // The .sh's design IS idempotent today (per code inspection):
    //   - mkdir -p (idempotent)
    //   - tar xzf (overwrites existing files)
    //   - chmod +x (overwrites)
    //   - ln -sf (overwrites symlink — `f` flag forces replace)
    //   - install_runtime_deps (idempotent — ldconfig pre-check short-circuits)
    //   - install -m 0755 -d /etc/apt/keyrings (idempotent)
    //
    // This test PINS that contract end-to-end so any future change that
    // accidentally breaks one of these idempotency invariants triggers a
    // CI failure.
    //
    // Test mechanism: stage tarball, run install twice in succession, assert:
    //   - Both runs exit 0
    //   - Both runs print "Verified: ... executable" (binary still functional)
    //   - Final state (binary + symlinks + dirs) matches single-install
    //   - No spurious error messages between runs
    //
    // Tier: 🟢 H. Reuses J.M.L.A.2 fixture + tarball builder; only adds
    // the second sequential RunInstallScript call.
    //
    // Expected runtime: ~2x J.M.L.A.2 (~1-2s; .sh is fast on warm cache).
    // ========================================================================

    [Fact]
    public void A8h_RerunInstaller_IdempotentlySucceeds()
    {
        if (!LinuxInstallScriptContext.IsAvailable) return;

        using var ctx = new LinuxInstallScriptContext();

        const string version = "1.6.0-rerun";
        var tarball = ctx.BuildInstallTarGz(version);
        ctx.Mirror.StagePreBuiltArchive(tarball);

        // ── Run 1: clean install ────────────────────────────────────────────
        var (exit1, output1) = ctx.RunInstallScript(version);

        exit1.ShouldBe(0,
            customMessage: $"first install run MUST succeed before idempotency can be tested. Got exit {exit1}.\noutput tail:\n{(output1.Length > 1000 ? "..." + output1.Substring(output1.Length - 1000) : output1)}");

        output1.ShouldContain("Verified: squid-tentacle executable",
            customMessage: "first run MUST log verification — sanity check before re-run.");

        File.Exists(Path.Combine(ctx.InstallDir, "Squid.Tentacle")).ShouldBeTrue("first run MUST install binary");
        File.Exists("/usr/local/bin/squid-tentacle").ShouldBeTrue("first run MUST create PATH symlink");

        // ── Run 2: re-run on existing state ────────────────────────────────
        // No cleanup between runs — the .sh's idempotency contract MUST
        // handle: existing INSTALL_DIR, existing symlinks, existing
        // /etc/squid-tentacle, existing /var/lib/squid-tentacle, existing
        // /etc/apt/keyrings.
        var (exit2, output2) = ctx.RunInstallScript(version);

        exit2.ShouldBe(0,
            customMessage: $"SECOND install run MUST succeed (idempotent contract). Got exit {exit2}. " +
                          $"If 1: regression in idempotency — likely tar xzf fails on existing files, OR ln -sf regressed to ln -s, OR a mkdir somewhere lost the -p flag. " +
                          $"If 2: arg parsing introduced a 'detect already installed' check that errors instead of proceeding. " +
                          $"output tail:\n{(output2.Length > 2000 ? "..." + output2.Substring(output2.Length - 2000) : output2)}");

        output2.ShouldContain("Verified: squid-tentacle executable",
            customMessage: "second run MUST also log verification — proves the binary is still runnable after re-extract. " +
                          "If absent: post-install verification step regressed OR the binary got corrupted by re-extraction.");

        // Reverse-assert: no obvious "already installed, abort" error.
        // Several common-but-bad regression patterns produce these strings.
        output2.ShouldNotContain("already installed",
            customMessage: "second run stdout MUST NOT contain 'already installed' — that signal indicates a regression where idempotency was broken by adding an error-on-existing-state check. " +
                          "Re-runs are a critical operator workflow (refresh, repair, fleet automation); they must succeed silently.");

        output2.ShouldNotContain("Error: install dir not empty",
            customMessage: "second run MUST NOT error on non-empty INSTALL_DIR. The .sh uses tar xzf which overwrites existing files; a regression to 'fail-if-not-empty' breaks every fleet refresh.");

        // Final state assertions — same as single-install.
        File.Exists(Path.Combine(ctx.InstallDir, "Squid.Tentacle")).ShouldBeTrue(
            customMessage: $"binary MUST still exist at {ctx.InstallDir}/Squid.Tentacle after re-install. If absent: re-extract failed silently OR rm-then-extract pattern dropped the binary mid-flight.");

        File.Exists("/usr/local/bin/squid-tentacle").ShouldBeTrue(
            customMessage: "/usr/local/bin/squid-tentacle MUST still exist after re-install. ln -sf overwrites; if symlink is gone, ln -sf regressed to plain ln (fails on existing target).");

        Directory.Exists("/etc/squid-tentacle").ShouldBeTrue("CONFIG_DIR persists across re-installs");
        Directory.Exists("/var/lib/squid-tentacle").ShouldBeTrue("STATE_DIR persists across re-installs");

        ctx.MarkClean();
    }

    // ========================================================================
    // A11.h-Linux — Sudoers + service-user happy path: install with
    //                CREATE_USER=yes creates the squid-tentacle system
    //                user AND installs a visudo-validated sudoers file
    //
    // This is THE prerequisite for the in-UI upgrade flow. The 11
    // upgrade-flow E2E tests in J.L.E.7-19 all use a custom systemd unit
    // running as the test user, but production runs the agent as the
    // dedicated `squid-tentacle` system user. The upgrade path's sudo
    // calls (systemd-run --scope, apt-get install squid-tentacle=*,
    // dpkg -i --force-downgrade, mv to /var/lib/squid-tentacle/...) ALL
    // require the matching sudoers file to be installed correctly.
    //
    // Without this E2E pin, regressions in any of the following ship
    // silently (operator's first upgrade attempt is the catch site, and
    // by then they've already deployed the broken installer to the fleet):
    //
    //   - Sudoers template's heredoc breaks (e.g. unescaped backtick
    //     introduced — caught a real prod bug pinned by
    //     InstallTentacleSudoersTests unit; this test confirms the unit's
    //     pin actually flows through the .sh's heredoc + visudo chain
    //     end-to-end)
    //   - visudo -c is removed/replaced with no validation → bad sudoers
    //     gets written → first upgrade prompts for password and hangs
    //   - SERVICE_USER detection regresses → sudoers block skipped even
    //     though useradd succeeded → upgrade prompts for password
    //   - useradd flag drift (e.g. --no-create-home dropped) → /home/
    //     pollution; test's user enumeration catches this
    //
    // Existing coverage:
    //   - InstallTentacleSudoersTests (unit) validates the GENERATED
    //     sudoers content shape
    //   - This test confirms the WHOLE CHAIN: .sh's heredoc renders →
    //     temp file passes visudo -c → moves to /etc/sudoers.d/ →
    //     operator-visible "Installed upgrade sudoers rule" log line
    //
    // Test mechanism: re-run install with CREATE_USER=yes (overrides the
    // fixture's default CREATE_USER=no), assert:
    //   - exit 0
    //   - getent passwd squid-tentacle returns the system user
    //   - /etc/sudoers.d/squid-tentacle-upgrade exists with mode 0440
    //   - file content has expected sudoers rule prefix
    //   - stdout logs "Created system user: squid-tentacle"
    //   - stdout logs "Installed upgrade sudoers rule" (NOT "Warning:
    //     generated sudoers rule failed validation")
    //
    // Cleanup matrix already handles userdel + sudoers rm in fixture's
    // Dispose (J.M.L.A.5 added userdel).
    //
    // Tier: 🟢 H (Rule 12.4) — drives real .sh + real useradd + real
    // visudo + real /etc/sudoers.d/. Heaviest install test
    // because it actually mutates the system identity DB.
    //
    // Expected runtime: ~5-10s.
    // ========================================================================

    [Fact]
    public void A11h_SudoersAndServiceUserInstalled_VisudoValidates()
    {
        if (!LinuxInstallScriptContext.IsAvailable) return;

        using var ctx = new LinuxInstallScriptContext();

        const string version = "1.6.0-sudoers";
        var tarball = ctx.BuildInstallTarGz(version);
        ctx.Mirror.StagePreBuiltArchive(tarball);

        // Override fixture default: enable user + sudoers creation.
        // Fixture default is CREATE_USER=no (smaller pollution surface
        // for most install tests); this test exercises the real
        // production path that depends on the system user existing.
        var (exitCode, output) = ctx.RunInstallScript(version, extraEnv: new Dictionary<string, string>
        {
            ["CREATE_USER"] = "yes"
        });

        exitCode.ShouldBe(0,
            customMessage: $"sudoers + service-user install MUST exit 0. Got exit {exitCode}. " +
                          $"If 1: useradd or sudoers visudo failed mid-flight. " +
                          $"output tail (last 2k chars):\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // ── Service user assertions ────────────────────────────────────────
        // `getent passwd squid-tentacle` returns 0 if the user exists. The
        // .sh's useradd is conditional on `getent passwd $SERVICE_USER`
        // failing (i.e. user doesn't exist) — first install run should
        // create it.
        var getentPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "getent",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        getentPsi.ArgumentList.Add("passwd");
        getentPsi.ArgumentList.Add("squid-tentacle");

        using (var getent = System.Diagnostics.Process.Start(getentPsi))
        {
            getent.ShouldNotBeNull();
            var getentStdout = getent.StandardOutput.ReadToEnd();
            getent.WaitForExit(5_000).ShouldBeTrue("getent must complete within 5s");
            getent.ExitCode.ShouldBe(0,
                customMessage: $"squid-tentacle system user MUST exist after install with CREATE_USER=yes. " +
                              $"`getent passwd squid-tentacle` exited {getent.ExitCode}. " +
                              "If non-zero: useradd silently failed (likely missing on the host, OR --system flag rejected). " +
                              "Production impact: agent runs as root instead of the dedicated system user — privilege containment broken.");

            // System user MUST be `--no-create-home` (no /home/squid-tentacle).
            // getent passwd output: name:passwd:uid:gid:gecos:home:shell
            // Sanity: home should NOT be /home/squid-tentacle.
            getentStdout.ShouldNotContain("/home/squid-tentacle",
                customMessage: $"squid-tentacle MUST be a `--no-create-home` system user. getent shows /home/squid-tentacle home dir, suggesting useradd's --no-create-home flag regressed. " +
                              $"getent stdout: {getentStdout.Trim()}");
        }

        // Operator-visible "Created system user" log MUST appear (proves
        // useradd actually ran in this invocation, not idempotent skip).
        output.ShouldContain("Created system user: squid-tentacle",
            customMessage: $"stdout MUST log 'Created system user: squid-tentacle' on the first install run that creates the user. " +
                          $"If absent: useradd block skipped (perhaps user already existed pre-test, but cleanup matrix should have removed it).");

        // ── Sudoers assertions ─────────────────────────────────────────────
        const string sudoersPath = "/etc/sudoers.d/squid-tentacle-upgrade";
        File.Exists(sudoersPath).ShouldBeTrue(
            customMessage: $"sudoers file MUST exist at {sudoersPath} after install with SERVICE_USER created. " +
                          "If absent: visudo -c rejected the generated content (template bug — operator-visible 'Warning: generated sudoers rule failed validation' should appear in stdout). " +
                          "Production impact: in-UI upgrades hang on password prompt forever.");

        // Verify the visudo path actually fired SUCCESSFULLY — log line
        // distinguishes from the failure-mode message that .sh emits if
        // visudo -c rejects.
        output.ShouldContain("Installed upgrade sudoers rule",
            customMessage: $"stdout MUST contain 'Installed upgrade sudoers rule' confirming visudo -c accepted the generated content + the file was moved to {sudoersPath}. " +
                          $"If absent: .sh logged the failure-path message instead — visudo rejected the heredoc-rendered content (likely template regression: e.g. unescaped backtick, malformed colon). " +
                          $"output tail:\n{(output.Length > 2000 ? "..." + output.Substring(output.Length - 2000) : output)}");

        // Reverse-assert: failure-path message MUST NOT appear (catches
        // the case where .sh logs both — proceed-with-warning AND install).
        output.ShouldNotContain("Warning: generated sudoers rule failed validation",
            customMessage: "stdout contains visudo-validation-failure warning — install proceeded but sudoers wasn't actually installed. " +
                          "Production impact: agent's first upgrade hangs on password prompt because sudoers file is absent.");

        // Mode 0440 is required by visudo to load the file. .sh's `chmod
        // 440 ${SUDOERS_FILE}.tmp` sets this before mv. Verify the final
        // file's mode.
        var sudoersInfo = new FileInfo(sudoersPath);
        sudoersInfo.UnixFileMode.ShouldBe(System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.GroupRead,
            customMessage: $"sudoers file mode MUST be 0440 (user-read + group-read, no execute, no other). Got {sudoersInfo.UnixFileMode}. " +
                          "If wrong: visudo at runtime ignores the file (logs 'unsafe permissions' warning) and the rule has no effect. " +
                          "Operator impact: in-UI upgrade hangs on password prompt despite the file being on disk.");

        // Sanity: file content has expected sudoers rule prefix. The full
        // shape is unit-tested by InstallTentacleSudoersTests; here we
        // just confirm the heredoc rendered SOME plausible sudoers content.
        var sudoersContent = File.ReadAllText(sudoersPath);
        sudoersContent.ShouldContain("squid-tentacle ALL=(root) NOPASSWD:",
            customMessage: $"sudoers file content MUST contain at least one 'squid-tentacle ALL=(root) NOPASSWD:' rule. " +
                          $"If absent: heredoc didn't expand SERVICE_USER, OR template regressed. " +
                          $"Content (first 500 chars):\n{sudoersContent.Substring(0, Math.Min(500, sudoersContent.Length))}");

        sudoersContent.ShouldContain("/usr/bin/systemd-run --scope",
            customMessage: "sudoers file MUST contain the scope-detach rule (the SINGLE hard privilege the upgrade flow needs). " +
                          "If absent: upgrade-linux-tentacle.sh's `sudo systemd-run --scope` will prompt for password.");

        ctx.MarkClean();
    }
}
