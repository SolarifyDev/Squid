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
}
