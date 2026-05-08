using Squid.WindowsTentacleE2ETests.Infrastructure;

namespace Squid.WindowsTentacleE2ETests;

/// <summary>
/// Phase 13 PR-2 — smoke tests for <see cref="WindowsTentacleBinaryFixture"/>.
/// Tier 🔵 Fixture-only (Rule 12) — proves the build pipeline works
/// (fixture compiles + binary runs + reports the expected version)
/// before downstream Phase 13 PR-3 (real-binary-as-polling-agent)
/// consumes it.
///
/// <para>Why a smoke layer matters: the binary is built via
/// <c>dotnet publish</c> which has many failure modes (missing SDK,
/// network restore failure, csproj target drift, RID mismatch). A
/// dedicated smoke test surfaces these failures BEFORE the bigger
/// downstream tests start using the binary — much easier to debug
/// "build failed" at smoke vs "polling-agent test mysteriously
/// failed".</para>
///
/// <para>Mirrors <c>Squid.LinuxTentacleE2ETests.TentacleLinuxBinarySmokeE2ETests</c>:
/// the fixture is fixture-only-tier; downstream tests that USE the
/// fixture for production-flow E2E are tier 🟢 H.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.TentacleBinary)]
[Collection(WindowsTentacleHostStateCollection.Name)]
public sealed class TentacleWindowsBinarySmokeE2ETests
{
    // ========================================================================
    // Binary_Version_PrintsExpectedString
    //
    // Smallest viable smoke: build the binary + run `version` + assert
    // the canonical-version string the production CLI prints matches the
    // fixture's published version stamp.
    //
    // Catches:
    //   - dotnet publish fails entirely (build pipeline regression)
    //   - PublishSingleFile target produces a different output path
    //     (e.g. the win-x64 self-contained bundle layout changed)
    //   - VersionCommand regression (CLI prints something other than
    //     AssemblyVersion.Canonical)
    //   - -p:Version flag doesn't propagate to AssemblyName.Version
    //
    // Why this test FIRST (before PR-3 polling-agent test): the most
    // likely failure mode for PR-2 is the build itself; PR-3 would
    // then fail with cascading errors. Smoke isolates the build
    // success contract.
    //
    // Cross-OS parity: same expected output as the Linux smoke test
    // (BuildVersion="99.99.99"), proving AssemblyVersion.Canonical
    // computation is identical on Windows + Linux.
    // ========================================================================

    [Fact]
    public void Binary_Version_PrintsBuildVersionStamp()
    {
        if (!WindowsTentacleBinaryFixture.IsAvailable) return;

        var fixture = new WindowsTentacleBinaryFixture();

        var (exitCode, output) = fixture.Run("version");

        exitCode.ShouldBe(0,
            customMessage: $"`Squid.Tentacle.exe version` MUST exit 0. Got exit {exitCode}. " +
                          $"If non-zero: VersionCommand has a bug OR the binary is broken (build pipeline regression). " +
                          $"output:\n{output}");

        // VersionCommand reads AssemblyVersion.Canonical which is computed
        // from the numeric AssemblyName.Version (Major.Minor.Build.Revision)
        // with trailing `.0` Revision stripped. Pre-release suffixes from
        // AssemblyInformationalVersion are NOT included — this is consistent
        // with the Linux fixture's first-runner finding.
        output.Trim().ShouldBe(WindowsTentacleBinaryFixture.BuildVersion,
            customMessage: $"`Squid.Tentacle.exe version` stdout MUST be exactly '{WindowsTentacleBinaryFixture.BuildVersion}' " +
                          $"(the -p:Version stamp the fixture passes to dotnet publish, after AssemblyVersion.Canonical strips the trailing .0 revision). " +
                          $"Got: '{output.Trim()}'. " +
                          $"If different: AssemblyVersion.Canonical computation regressed, OR -p:Version flag is being overridden by the csproj's <Version> property, " +
                          $"OR Console.WriteLine is emitting extra text. " +
                          $"Full output:\n{output}");
    }

    // ========================================================================
    // Binary_NoArgs_ProducesOutput
    //
    // Sanity: invoking the binary with no args goes through CommandResolver's
    // default-to-RunCommand path. Without a valid registered config,
    // RunCommand should fail-fast OR start its loop — but it MUST produce
    // output (Serilog log line, error message, or help text). Pure crash /
    // hang would surface as empty stdout+stderr OR a Run() timeout.
    //
    // Why this matters: regressions in CommandResolver / Program.cs Main
    // (e.g. unhandled exception path that bypasses the help prompt) would
    // surface here as a hang or crash. Catches at smoke tier so downstream
    // tests don't have to deal with it.
    //
    // Note: we don't drive the binary into RUNNING state here — it tries
    // to start the agent which would eventually time out at the fixture's
    // 60s cap if config isn't valid. Instead we use `help` (or any short-
    // running command that exits cleanly without needing config).
    // ========================================================================

    [Fact]
    public void Binary_Help_ProducesUsageOutput()
    {
        if (!WindowsTentacleBinaryFixture.IsAvailable) return;

        var fixture = new WindowsTentacleBinaryFixture();

        // `help` is a short-running command that exits with usage text —
        // doesn't try to start the agent (which would need real config).
        // Catches: binary completely broken / segfaults / can't even
        // resolve commands.
        var (exitCode, output) = fixture.Run("help");

        // Help output should be printed (exit code can be 0 or non-zero
        // depending on whether help is treated as success — what matters
        // is output exists, not the exit code).
        output.ShouldNotBeNullOrEmpty(
            customMessage: $"binary `help` produced empty stdout+stderr. Either it hung past the 60s timeout " +
                          $"(already caught by Run's cap, would throw TimeoutException) OR crashed silently. Exit code: {exitCode}. " +
                          "Production binary's command-resolver path should always produce some output for `help`.");

        // Sanity: at least one of the documented commands appears.
        // PrintHelp lists every command via the registered ITentacleCommand
        // collection; any of these MUST appear in usage output.
        var hasAnyCommand = output.Contains("register", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("service", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("version", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("show-thumbprint", StringComparison.OrdinalIgnoreCase);

        hasAnyCommand.ShouldBeTrue(
            customMessage: $"`help` output MUST list at least one production command (register / service / version / show-thumbprint). " +
                          $"If absent: PrintHelp regressed, OR the command registry was emptied. " +
                          $"\noutput:\n{output}");
    }
}
