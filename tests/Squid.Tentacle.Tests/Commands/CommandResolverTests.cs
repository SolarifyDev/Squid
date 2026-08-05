using System.Linq;
using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Commands;

namespace Squid.Tentacle.Tests.Commands;

public class CommandResolverTests
{
    private static readonly ITentacleCommand[] Commands =
    [
        new RunCommand(),
        new ShowThumbprintCommand(),
        new ShowConfigCommand(),
        new NewCertificateCommand(),
        new RegisterCommand(),
        new ServiceCommand(),
        new VersionCommand()
    ];

    // ========================================================================
    // Help flags — all must route to help, never RunCommand
    // ========================================================================

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    [InlineData("HELP")]      // case-insensitive
    [InlineData("--Help")]    // case-insensitive
    public void Resolve_HelpFlag_RequestsHelp(string flag)
    {
        var route = CommandResolver.Resolve(Commands, [flag]);

        route.HelpRequested.ShouldBeTrue();
        route.Command.ShouldBeNull();
        route.UnknownCommand.ShouldBeNull();
    }

    [Fact]
    public void IsHelpFlag_EmptyString_ReturnsFalse()
    {
        CommandResolver.IsHelpFlag("").ShouldBeFalse();
        CommandResolver.IsHelpFlag(null).ShouldBeFalse();
    }

    // ========================================================================
    // Version flag — must route to VersionCommand, never RunCommand
    // ========================================================================

    [Theory]
    [InlineData("--version")]
    [InlineData("--VERSION")]   // case-insensitive, like the help flags
    public void Resolve_VersionFlag_RoutesToVersionCommand(string flag)
    {
        // Without this route --version falls into the `-` → run default and STARTS THE AGENT:
        // the probe captures no version, and a caller piping the output hangs on a process
        // that never exits. That cost a production hang in 1.4.1/1.4.2, and is why
        // deploy/packaging/after-install.sh spells it as the bare `version` verb with a
        // timeout and </dev/null.
        var route = CommandResolver.Resolve(Commands, [flag]);

        route.Command.ShouldBeOfType<VersionCommand>(
            customMessage: "--version must report the version, not start the agent.");
        route.HelpRequested.ShouldBeFalse();
        route.UnknownCommand.ShouldBeNull();
    }

    [Fact]
    public void Resolve_VersionVerb_StillRoutesToVersionCommand()
    {
        // The bare verb is what every existing caller uses (postinst, install scripts, CI).
        // The alias must not disturb it.
        var route = CommandResolver.Resolve(Commands, ["version"]);

        route.Command.ShouldBeOfType<VersionCommand>();
        route.RemainingArgs.ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_VersionFlag_NoVersionCommandRegistered_FallsBackToRun()
    {
        // A command set without a VersionCommand keeps today's behaviour rather than
        // throwing — the alias is additive, never a new failure mode.
        ITentacleCommand[] withoutVersion = [new RunCommand(), new ServiceCommand()];

        var route = CommandResolver.Resolve(withoutVersion, ["--version"]);

        route.Command.ShouldBeOfType<RunCommand>();
        route.RemainingArgs.ShouldBe(["--version"]);
    }

    [Fact]
    public void IsVersionFlag_ShortFormAndEmpty_ReturnFalse()
    {
        // -v stays an ordinary config flag: it is already pinned as one by
        // Resolve_LeadingConfigFlag_DefaultsToRun_AndPassesAllArgsThrough, and it means
        // "verbose" in most CLIs. Claiming it for version would be a breaking change.
        CommandResolver.IsVersionFlag("-v").ShouldBeFalse();
        CommandResolver.IsVersionFlag("-V").ShouldBeFalse();
        CommandResolver.IsVersionFlag("version").ShouldBeFalse();  // the verb, not a flag
        CommandResolver.IsVersionFlag("").ShouldBeFalse();
        CommandResolver.IsVersionFlag(null).ShouldBeFalse();
    }

    // ========================================================================
    // Default → RunCommand
    // ========================================================================

    [Fact]
    public void Resolve_NoArgs_DefaultsToRunCommand()
    {
        var route = CommandResolver.Resolve(Commands, []);

        route.Command.ShouldBeOfType<RunCommand>();
        route.RemainingArgs.ShouldBeEmpty();
        route.HelpRequested.ShouldBeFalse();
    }

    [Theory]
    [InlineData("--Tentacle:Flavor=LinuxTentacle")]
    [InlineData("--server")]
    [InlineData("-v")]
    public void Resolve_LeadingConfigFlag_DefaultsToRun_AndPassesAllArgsThrough(string leadingFlag)
    {
        var route = CommandResolver.Resolve(Commands, [leadingFlag, "value"]);

        route.Command.ShouldBeOfType<RunCommand>();
        route.RemainingArgs.ShouldBe([leadingFlag, "value"]);
    }

    [Fact]
    public void Resolve_ExplicitRunVerb_StripsVerb()
    {
        var route = CommandResolver.Resolve(Commands, ["run", "--Tentacle:Flavor=LinuxTentacle"]);

        route.Command.ShouldBeOfType<RunCommand>();
        route.RemainingArgs.ShouldBe(["--Tentacle:Flavor=LinuxTentacle"]);
    }

    // ========================================================================
    // Known verbs
    // ========================================================================

    [Theory]
    [InlineData("show-thumbprint", typeof(ShowThumbprintCommand))]
    [InlineData("show-config", typeof(ShowConfigCommand))]
    [InlineData("new-certificate", typeof(NewCertificateCommand))]
    [InlineData("register", typeof(RegisterCommand))]
    [InlineData("service", typeof(ServiceCommand))]
    [InlineData("RUN", typeof(RunCommand))]              // case-insensitive
    [InlineData("Show-Thumbprint", typeof(ShowThumbprintCommand))]
    public void Resolve_KnownVerb_RoutesToCommand(string verb, Type expectedType)
    {
        var route = CommandResolver.Resolve(Commands, [verb]);

        route.Command.GetType().ShouldBe(expectedType);
        route.RemainingArgs.ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_VerbWithArgs_StripsVerbAndPassesRemaining()
    {
        var route = CommandResolver.Resolve(Commands, ["register", "--server", "https://squid:7078", "--api-key", "KEY"]);

        route.Command.ShouldBeOfType<RegisterCommand>();
        route.RemainingArgs.ShouldBe(["--server", "https://squid:7078", "--api-key", "KEY"]);
    }

    // ========================================================================
    // Unknown verbs
    // ========================================================================

    [Fact]
    public void Resolve_UnknownVerb_ReportsUnknown()
    {
        var route = CommandResolver.Resolve(Commands, ["frobnicate"]);

        route.UnknownCommand.ShouldBe("frobnicate");
        route.Command.ShouldBeNull();
        route.HelpRequested.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_UnknownVerb_LowercasesBeforeReporting()
    {
        var route = CommandResolver.Resolve(Commands, ["FROBNICATE"]);

        route.UnknownCommand.ShouldBe("frobnicate");
    }

    // ========================================================================
    // Regression — the bug we fixed
    // ========================================================================

    [Fact]
    public void Resolve_DashDashHelp_DoesNotRouteToRunCommand()
    {
        // Before the fix: --help started with "--" so it was routed to RunCommand,
        // which started the agent instead of printing help.
        var route = CommandResolver.Resolve(Commands, ["--help"]);

        route.HelpRequested.ShouldBeTrue();
        route.Command.ShouldBeNull();
    }

    [Fact]
    public void Resolve_DashHHelp_DoesNotRouteToRunCommand()
    {
        var route = CommandResolver.Resolve(Commands, ["-h"]);

        route.HelpRequested.ShouldBeTrue();
    }
}
