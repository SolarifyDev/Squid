using Squid.Calamari.ServiceMessages;

namespace Squid.Calamari.Tests.Calamari.ServiceMessages;

public class ServiceMessageParserTests
{
    [Theory]
    [InlineData("##squid[setVariable name='X' value='1']")]
    public void IsServiceMessage_SquidPrefix_ReturnsTrue(string line)
    {
        ServiceMessageParser.IsServiceMessage(line).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("regular output")]
    [InlineData("## not a directive")]
    public void IsServiceMessage_NonDirective_ReturnsFalse(string line)
    {
        ServiceMessageParser.IsServiceMessage(line).ShouldBeFalse();
    }

    [Fact]
    public void TryParse_SquidPrefix_ParsesCorrectly()
    {
        var line = "##squid[setVariable name='MyVar' value='Hello' sensitive='True']";

        var result = ServiceMessageParser.TryParse(line);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("MyVar");
        result.Value.ShouldBe("Hello");
        result.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_SquidPrefix_NotSensitive_DefaultsFalse()
    {
        var line = "##squid[setVariable name='Var' value='Val']";

        var result = ServiceMessageParser.TryParse(line);

        result.ShouldNotBeNull();
        result.IsSensitive.ShouldBeFalse();
    }
}
