using Squid.Calamari.Host;

namespace Squid.Calamari.Tests.Calamari.Host;

public class CommandLineArgumentsTests
{
    [Fact]
    public void ParseKeyValueArgs_SupportsEqualsSyntax()
    {
        var parsed = CommandLineArguments.ParseKeyValueArgs(["--file=app.yaml", "--namespace=demo"]);

        parsed["--file"].ShouldBe("app.yaml");
        parsed["--namespace"].ShouldBe("demo");
    }

    [Fact]
    public void ParseKeyValueArgs_SupportsSplitSyntax()
    {
        var parsed = CommandLineArguments.ParseKeyValueArgs(["--file", "app.yaml", "--namespace", "demo"]);

        parsed["--file"].ShouldBe("app.yaml");
        parsed["--namespace"].ShouldBe("demo");
    }

    [Fact]
    public void ContainsHelpToken_DetectsCommonHelpForms()
    {
        CommandLineArguments.ContainsHelpToken(["--help"]).ShouldBeTrue();
        CommandLineArguments.ContainsHelpToken(["-h"]).ShouldBeTrue();
        CommandLineArguments.ContainsHelpToken(["help"]).ShouldBeTrue();
        CommandLineArguments.ContainsHelpToken(["--file", "x.yaml"]).ShouldBeFalse();
    }
}
