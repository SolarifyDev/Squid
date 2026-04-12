using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class SensitiveOutputVariableTests
{
    private static readonly ServiceMessageParser Parser = new();

    // ========== ServiceMessageParser ==========

    [Fact]
    public void ParseOutputVariables_SensitiveTrue_FlagPreserved()
    {
        var lines = new List<string>
        {
            "##squid[setVariable name='Token' value='secret123' sensitive='True']"
        };

        var result = Parser.ParseOutputVariables(lines);

        result.ShouldContainKey("Token");
        result["Token"].Value.ShouldBe("secret123");
        result["Token"].IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void ParseOutputVariables_SensitiveFalse_FlagPreserved()
    {
        var lines = new List<string>
        {
            "##squid[setVariable name='Output' value='hello' sensitive='False']"
        };

        var result = Parser.ParseOutputVariables(lines);

        result["Output"].IsSensitive.ShouldBeFalse();
    }

    [Fact]
    public void ParseOutputVariables_NoSensitiveAttribute_DefaultsFalse()
    {
        var lines = new List<string>
        {
            "##squid[setVariable name='Output' value='hello']"
        };

        var result = Parser.ParseOutputVariables(lines);

        result["Output"].IsSensitive.ShouldBeFalse();
    }

    [Fact]
    public void ParseOutputVariables_SensitiveCaseInsensitive()
    {
        var lines = new List<string>
        {
            "##squid[setVariable name='Token' value='abc' sensitive='true']"
        };

        var result = Parser.ParseOutputVariables(lines);

        result["Token"].IsSensitive.ShouldBeTrue();
    }

}
