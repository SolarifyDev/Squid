using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class ScriptSyntaxHelperTests
{
    private static DeploymentActionDto CreateAction(string syntax = null)
    {
        var action = new DeploymentActionDto
        {
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (syntax != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.Script.Syntax",
                PropertyValue = syntax
            });
        }

        return action;
    }

    [Theory]
    [InlineData("Bash", ScriptSyntax.Bash)]
    [InlineData("PowerShell", ScriptSyntax.PowerShell)]
    [InlineData("CSharp", ScriptSyntax.CSharp)]
    [InlineData("FSharp", ScriptSyntax.FSharp)]
    [InlineData("Python", ScriptSyntax.Python)]
    [InlineData("bash", ScriptSyntax.Bash)]
    [InlineData("csharp", ScriptSyntax.CSharp)]
    public void ResolveSyntax_KnownLanguages(string input, ScriptSyntax expected)
    {
        var action = CreateAction(input);
        ScriptSyntaxHelper.ResolveSyntax(action).ShouldBe(expected);
    }

    [Fact]
    public void ResolveSyntax_NullProperty_DefaultsBash()
    {
        var action = CreateAction();
        ScriptSyntaxHelper.ResolveSyntax(action).ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public void ResolveSyntax_Unknown_DefaultsBash()
    {
        var action = CreateAction("Ruby");
        ScriptSyntaxHelper.ResolveSyntax(action).ShouldBe(ScriptSyntax.Bash);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("99")]
    public void ResolveSyntax_NumericString_DefaultsBash(string numericValue)
    {
        var action = CreateAction(numericValue);
        ScriptSyntaxHelper.ResolveSyntax(action).ShouldBe(ScriptSyntax.Bash);
    }

    [Theory]
    [InlineData(ScriptSyntax.Bash, true)]
    [InlineData(ScriptSyntax.PowerShell, true)]
    [InlineData(ScriptSyntax.CSharp, false)]
    [InlineData(ScriptSyntax.FSharp, false)]
    [InlineData(ScriptSyntax.Python, false)]
    public void IsShellSyntax_Classification(ScriptSyntax syntax, bool expected)
    {
        ScriptSyntaxHelper.IsShellSyntax(syntax).ShouldBe(expected);
    }
}
