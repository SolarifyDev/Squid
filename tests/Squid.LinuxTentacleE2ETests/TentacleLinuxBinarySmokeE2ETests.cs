using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.M.L.B.0 — smoke tests for <see cref="LinuxTentacleBinaryFixture"/>.
/// Tier 🔵 Fixture-only (Rule 12) — proves the build pipeline works
/// (fixture compiles + binary runs + reports the expected version)
/// before downstream Section B/C/G tests consume it.
///
/// <para>Why a smoke layer matters: the binary is built via
/// <c>dotnet publish</c> which has many failure modes (missing SDK,
/// network restore failure, csproj target drift, RID mismatch). A
/// dedicated smoke test surfaces these failures BEFORE the bigger
/// downstream tests start using the binary — much easier to debug
/// "build failed" at smoke vs "register test mysteriously failed".</para>
///
/// <para>Mirrors the <see cref="LinuxServiceFixtureSmokeE2ETests"/>
/// pattern: the fixture is fixture-only-tier; downstream tests that
/// USE the fixture for production-flow E2E are tier 🟢 H.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
[Collection(LinuxTentacleHostStateCollection.Name)]
public sealed class TentacleLinuxBinarySmokeE2ETests
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
    //   - VersionCommand regression (CLI prints something other than
    //     AssemblyVersion.Canonical)
    //   - -p:Version flag doesn't propagate to AssemblyInformationalVersion
    //
    // Why this test FIRST (before service / register tests): the most
    // likely failure mode for J.M.L.B.0 is the build itself; downstream
    // tests would then fail with cascading errors. Smoke isolates the
    // build success contract.
    // ========================================================================

    [Fact]
    public void Binary_Version_PrintsBuildVersionStamp()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;

        var fixture = new LinuxTentacleBinaryFixture();

        var (exitCode, output) = fixture.Run("version");

        exitCode.ShouldBe(0,
            customMessage: $"`squid-tentacle version` MUST exit 0. Got exit {exitCode}. " +
                          $"If non-zero: VersionCommand has a bug OR the binary is broken (build pipeline regression). " +
                          $"output:\n{output}");

        // VersionCommand reads AssemblyVersion.Canonical which is computed
        // from the numeric AssemblyName.Version (Major.Minor.Build.Revision)
        // with trailing `.0` Revision stripped. Pre-release suffixes from
        // AssemblyInformationalVersion are NOT included — caught by the
        // J.M.L.B.0 first runner where I'd assumed the suffix would
        // propagate. BuildVersion is now a numeric 3-part value chosen to
        // survive the Canonical computation unchanged.
        output.Trim().ShouldBe(LinuxTentacleBinaryFixture.BuildVersion,
            customMessage: $"`squid-tentacle version` stdout MUST be exactly '{LinuxTentacleBinaryFixture.BuildVersion}' " +
                          $"(the -p:Version stamp the fixture passes to dotnet publish, after AssemblyVersion.Canonical strips the trailing .0 revision). " +
                          $"Got: '{output.Trim()}'. " +
                          $"If different: AssemblyVersion.Canonical computation regressed, OR -p:Version flag is being overridden by the csproj's <Version> property, " +
                          $"OR Console.WriteLine is emitting extra text. " +
                          $"Full output:\n{output}");
    }

    // ========================================================================
    // Binary_NoArgs_DoesNotCrash
    //
    // Sanity: invoking the binary with no args goes through CommandResolver's
    // default-to-RunCommand path. RunCommand starts the agent; without a
    // valid config it should fail-fast with a clear error, NOT segfault /
    // hang. We invoke with a 5s window (binary should respond well within
    // that with a config-missing error) then kill it.
    //
    // Why this matters: regressions in CommandResolver / Program.cs Main
    // (e.g. unhandled exception path that bypasses the help prompt) would
    // surface here as a hang or crash. Catches at smoke tier so downstream
    // service tests don't have to deal with it.
    // ========================================================================

    [Fact]
    public void Binary_NoArgs_FailsClearlyWithoutHanging()
    {
        if (!LinuxTentacleBinaryFixture.IsAvailable) return;

        var fixture = new LinuxTentacleBinaryFixture();

        // Run without args. Expected: RunCommand path tries to start the
        // agent with default config → fails fast (config not found / no
        // instance registered) within seconds. We don't assert on the
        // exact exit code (different config-error code paths), just that
        // SOMETHING happens within the timeout (the fixture's 60s budget
        // is the worst-case ceiling).
        var (exitCode, output) = fixture.Run(/* no args */);

        // Either a clean non-zero (config error) or 0 (if it somehow
        // started successfully — unlikely without setup). The point is:
        // didn't hang past the fixture timeout, didn't crash without
        // any output.
        output.ShouldNotBeNullOrEmpty(
            customMessage: $"binary with no args produced empty stdout+stderr. Either it hung past the timeout (already caught by Run's 60s cap) " +
                          $"OR crashed silently (segfault). Exit code: {exitCode}. " +
                          "Production binary's RunCommand path should always produce some log output before exiting.");
    }
}
