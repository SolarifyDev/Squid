using Squid.Calamari.Host;

namespace Squid.Calamari.Tests.Calamari.Host;

/// <summary>
/// P0-B.2 regression guard (2026-04-24 audit). The tentacle sends the sensitive-
/// variable encryption password to Calamari via environment variable rather than
/// the old <c>--password=</c> argv. Argv was visible in <c>ps aux</c> /
/// <c>/proc/&lt;pid&gt;/cmdline</c> (typically world-readable) — a privilege-
/// free secret leak on every multi-tenant host.
///
/// <para>These tests pin:
/// <list type="bullet">
///   <item>The env-var name matches the tentacle's copy (literal must stay identical
///         on both sides — drift silently breaks sensitive-variable decryption).</item>
///   <item>Env var wins over argv when both are set (tentacle never sets both
///         post-fix; an env-var-loses design would let a test accidentally expose
///         the prod path to argv).</item>
///   <item>Env var null / empty / whitespace falls through to argv — avoids a
///         mistakenly-unset env var from blanking out a real argv password during
///         tests or manual invocation.</item>
/// </list>
/// </para>
/// </summary>
public sealed class RunScriptCliSensitivePasswordEnvVarTests
{
    [Fact]
    public void SensitivePasswordEnvVar_ConstantNamePinned()
    {
        // Hard-pin so a rename shows up as a compile-visible decision. The tentacle
        // side (LocalScriptService.CalamariSensitivePasswordEnvVar) pins the same
        // literal — drift would silently break sensitive-variable decryption.
        RunScriptCliCommandHandler.SensitivePasswordEnvVar.ShouldBe("SQUID_CALAMARI_SENSITIVE_PASSWORD");
    }

    [Fact]
    public void ResolvePassword_EnvVarSet_WinsOverArgv()
    {
        // Tentacle sets env var. A stale test script supplying --password shouldn't
        // override what the operator-facing tentacle path supplies.
        var result = RunScriptCliCommandHandler.ResolvePassword(
            argvPassword: "argv-wrong",
            envPassword: "env-correct");

        result.ShouldBe("env-correct",
            customMessage: "env var is the canonical prod path — must win over argv");
    }

    [Fact]
    public void ResolvePassword_NoEnvVar_FallsBackToArgv()
    {
        // Manual invocation / test harness — env var unset, --password= supplied.
        var result = RunScriptCliCommandHandler.ResolvePassword(
            argvPassword: "argv-only",
            envPassword: null);

        result.ShouldBe("argv-only",
            customMessage: "no env var → fall back to --password so manual invocation keeps working");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ResolvePassword_EnvVarBlank_FallsBackToArgv(string envValue)
    {
        // A blank / whitespace env var is treated as "not set" — otherwise a
        // mistakenly-unset-but-present env var during tests blanks out a real argv
        // password, producing a confusing "wrong password" error at decrypt time.
        var result = RunScriptCliCommandHandler.ResolvePassword(
            argvPassword: "argv-correct",
            envPassword: envValue);

        result.ShouldBe("argv-correct");
    }

    [Fact]
    public void ResolvePassword_NeitherSet_ReturnsNull()
    {
        // Happy path for scripts without sensitive vars — no password supplied from
        // any source, decrypt path simply doesn't run.
        var result = RunScriptCliCommandHandler.ResolvePassword(
            argvPassword: null,
            envPassword: null);

        result.ShouldBeNull();
    }
}
