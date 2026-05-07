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
}
