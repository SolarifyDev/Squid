using Shouldly;
using Squid.Tentacle.Commands;
using Xunit;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

/// <summary>
/// Unit-tier pins for <see cref="RegisterCommand.ExpandShorthandArgs"/> — the
/// translator that turns the operator's <c>--role X --role Y --role Z</c>
/// shorthand into the canonical <c>--Tentacle:Roles=X,Y,Z</c> form before it
/// reaches <c>ConfigurationBuilder.AddCommandLine</c>.
///
/// <para>
/// <b>Why a unit tier in addition to the E2E</b>: the E2E counterparts
/// (<c>TentacleRegisterE2ETests.RepeatedRoleFlags_*</c>,
/// <c>RepeatedEnvironmentFlags_*</c>) drive the real <c>Squid.Tentacle.exe</c>
/// against <c>StubSquidServer</c> and prove the network-level contract. That's
/// the contract-of-record. But each E2E launch takes ~5-10s; unit-tier assertions
/// here run in single-digit milliseconds and locate a regression precisely on
/// the <c>ExpandShorthandArgs</c> source instead of "somewhere between CLI and
/// server payload". Both layers matter.
/// </para>
///
/// <para>
/// <b>If a case here fails</b>: open <c>RegisterCommand.AccumulatingConfigKeys</c>
/// and the bucket-merge loop in <c>ExpandShorthandArgs</c>. The accumulation
/// path is the only Phase 3 change — if a unit case regresses, the production
/// fix has been silently reverted.
/// </para>
/// </summary>
public class RegisterCommandExpandShorthandArgsTests
{
    [Fact]
    public void SingleRoleFlag_EmitsInlineConfigKey_OrderPreserved()
    {
        var input = new[] { "--role", "web-server", "--api-key", "K" };

        var expanded = RegisterCommand.ExpandShorthandArgs(input);

        // Single --role goes through the accumulator (1-element bucket), so it's
        // emitted at the END of the result array, not in-place. The non-accumulating
        // --api-key stays in-place. Both produce a config arg.
        expanded.ShouldContain("--Tentacle:ApiKey=K",
            customMessage: "non-accumulating --api-key MUST emit as an inline --Tentacle:ApiKey arg");
        expanded.ShouldContain("--Tentacle:Roles=web-server",
            customMessage: "single --role MUST emit as --Tentacle:Roles=<value> with no spurious comma");
    }

    [Fact]
    public void TwoRoleFlags_AccumulatedIntoCommaSeparatedSingleArg()
    {
        var input = new[]
        {
            "--server", "https://test.example",
            "--role", "first",
            "--role", "second",
        };

        var expanded = RegisterCommand.ExpandShorthandArgs(input);

        expanded.ShouldContain("--Tentacle:Roles=first,second",
            customMessage:
                "two --role flags MUST be joined into a SINGLE arg --Tentacle:Roles=first,second. " +
                "AddCommandLine would otherwise keep only the LAST value via flat-key semantics — " +
                "the bug this regression test guards against.");

        // Non-accumulating keys still pass through inline.
        expanded.ShouldContain("--Tentacle:ServerUrl=https://test.example",
            customMessage: "non-accumulating --server still emits inline");
    }

    [Fact]
    public void ThreeRoleFlags_AllValuesPreservedInOrder()
    {
        var input = new[]
        {
            "--role", "web-server",
            "--role", "db-replica",
            "--role", "monitoring",
        };

        var expanded = RegisterCommand.ExpandShorthandArgs(input);

        expanded.ShouldContain("--Tentacle:Roles=web-server,db-replica,monitoring",
            customMessage:
                "three --role flags MUST accumulate in operator's CLI order. " +
                "If the joined order has been alphabetized or reversed, the bucket " +
                "loop in ExpandShorthandArgs has changed semantics — verify intent.");
    }

    [Fact]
    public void ThreeEnvironmentFlags_AllValuesPreservedInOrder()
    {
        var input = new[]
        {
            "--environment", "production",
            "--environment", "us-east",
            "--environment", "canary",
        };

        var expanded = RegisterCommand.ExpandShorthandArgs(input);

        expanded.ShouldContain("--Tentacle:Environments=production,us-east,canary",
            customMessage:
                "--environment is the OTHER list-valued shorthand and MUST accumulate identically. " +
                "If only --role accumulates and --environment regresses to last-wins, the " +
                "AccumulatingConfigKeys set has likely been narrowed.");
    }

    [Fact]
    public void RepeatedNonAccumulatingFlag_LastWins_Unchanged()
    {
        // Sanity check: non-accumulating keys must STILL behave as last-wins
        // via AddCommandLine. The accumulation path is OPT-IN per key —
        // accidentally widening it would silently merge a second --server URL
        // into a comma-joined value, which is meaningless for Tentacle:ServerUrl.
        var input = new[]
        {
            "--server", "https://first.example",
            "--server", "https://second.example",
        };

        var expanded = RegisterCommand.ExpandShorthandArgs(input);

        // Both inline args are emitted; AddCommandLine downstream picks the last.
        // The expander itself doesn't deduplicate non-accumulating keys.
        expanded.ShouldContain("--Tentacle:ServerUrl=https://first.example");
        expanded.ShouldContain("--Tentacle:ServerUrl=https://second.example");

        // Negative: --Tentacle:ServerUrl MUST NOT have been comma-merged.
        expanded.ShouldNotContain("--Tentacle:ServerUrl=https://first.example,https://second.example",
            customMessage:
                "--server is NOT in AccumulatingConfigKeys; comma-merging two server URLs would be " +
                "nonsensical. If this assertion fails, AccumulatingConfigKeys has been over-widened.");
    }

    [Fact]
    public void MixedSingleAndRepeated_BothBehavioursCoexist()
    {
        // The realistic CLI shape: a single --server, a single --api-key, three --role.
        var input = new[]
        {
            "--server", "https://test.example",
            "--api-key", "API-key-1",
            "--role", "web-server",
            "--role", "db-replica",
            "--environment", "production",
            "--flavor", "LinuxTentacle",
        };

        var expanded = RegisterCommand.ExpandShorthandArgs(input);

        expanded.ShouldContain("--Tentacle:ServerUrl=https://test.example");
        expanded.ShouldContain("--Tentacle:ApiKey=API-key-1");
        expanded.ShouldContain("--Tentacle:Flavor=LinuxTentacle");
        expanded.ShouldContain("--Tentacle:Roles=web-server,db-replica");
        expanded.ShouldContain("--Tentacle:Environments=production");
    }

    [Fact]
    public void UnknownFlag_PassesThroughVerbatim()
    {
        // Args that aren't in ArgMapping (e.g. raw --Some:Key=value pre-expanded
        // by the operator, or --force which is stripped upstream) must pass
        // through unchanged.
        var input = new[] { "--Some:Custom:Key=raw", "--unknown-flag", "value" };

        var expanded = RegisterCommand.ExpandShorthandArgs(input);

        expanded.ShouldContain("--Some:Custom:Key=raw");
        expanded.ShouldContain("--unknown-flag");
        expanded.ShouldContain("value");
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var expanded = RegisterCommand.ExpandShorthandArgs(Array.Empty<string>());

        expanded.ShouldBeEmpty();
    }

    [Fact]
    public void AccumulatingConfigKeys_ContainsRolesAndEnvironments_Pinned()
    {
        // PIN: only Tentacle:Roles and Tentacle:Environments are list-valued.
        // Adding a third (e.g. multiple --listening-host) requires updating
        // ArgMapping AND adding a parallel test in the E2E + unit suites.
        RegisterCommand.AccumulatingConfigKeys.ShouldContain("Tentacle:Roles");
        RegisterCommand.AccumulatingConfigKeys.ShouldContain("Tentacle:Environments");
        RegisterCommand.AccumulatingConfigKeys.Count.ShouldBe(2,
            customMessage:
                "AccumulatingConfigKeys now has !=2 entries. If a new list-valued shorthand has " +
                "been added, add a Theory case here and a Repeated*Flags_* test in TentacleRegisterE2ETests. " +
                "If you accidentally widened the set, you may have just regressed --server / --api-key " +
                "to comma-merging — re-read the accumulation doc-comment in RegisterCommand.");
    }
}
