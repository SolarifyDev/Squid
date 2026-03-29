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

    [Fact]
    public void ResolveSyntax_Bash_ReturnsBash()
    {
        var action = CreateAction("Bash");
        ScriptSyntaxHelper.ResolveSyntax(action).ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public void ResolveSyntax_PowerShell_ReturnsPowerShell()
    {
        var action = CreateAction("PowerShell");
        ScriptSyntaxHelper.ResolveSyntax(action).ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public void ResolveSyntax_CaseInsensitive_Bash()
    {
        var action = CreateAction("bash");
        ScriptSyntaxHelper.ResolveSyntax(action).ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public void ResolveSyntax_NullProperty_DefaultsPowerShell()
    {
        var action = CreateAction();
        ScriptSyntaxHelper.ResolveSyntax(action).ShouldBe(ScriptSyntax.PowerShell);
    }
}
