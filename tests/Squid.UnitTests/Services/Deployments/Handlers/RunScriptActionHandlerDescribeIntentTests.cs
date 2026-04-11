using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Handlers;

/// <summary>
/// Phase 9b — verifies that <see cref="RunScriptActionHandler"/> overrides
/// <c>DescribeIntentAsync</c> and emits a <see cref="RunScriptIntent"/> DIRECTLY,
/// without routing through the legacy <c>PrepareAsync</c> + <c>LegacyIntentAdapter</c>
/// seam. Critically, <see cref="RunScriptIntent.InjectRuntimeBundle"/> must be <c>true</c>
/// — the Phase 8 runtime bundle is always injected when the handler emits the intent
/// directly, unlike the adapter which defaults to <c>false</c>.
/// </summary>
public class RunScriptActionHandlerDescribeIntentTests
{
    private readonly RunScriptActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(
        string actionName = "run-web",
        string scriptBody = "echo hello",
        string syntax = null)
    {
        var action = new DeploymentActionDto
        {
            Name = actionName,
            ActionType = SpecialVariables.ActionTypes.Script,
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = SpecialVariables.Action.ScriptBody, PropertyValue = scriptBody }
            }
        };

        if (syntax != null)
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = SpecialVariables.Action.ScriptSyntax,
                PropertyValue = syntax
            });

        return action;
    }

    private static ActionExecutionContext CreateContext(
        string stepName = "Deploy Web",
        string actionName = "run-web",
        string scriptBody = "echo hello",
        string syntax = null)
    {
        return new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = stepName },
            Action = CreateAction(actionName, scriptBody, syntax)
        };
    }

    [Fact]
    public async Task DescribeIntentAsync_ReturnsRunScriptIntent()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ShouldBeOfType<RunScriptIntent>();
    }

    [Fact]
    public async Task DescribeIntentAsync_NameIsRunScript()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldBe("run-script");
    }

    [Fact]
    public async Task DescribeIntentAsync_InjectRuntimeBundle_IsTrue()
    {
        var ctx = CreateContext();

        var intent = (RunScriptIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.InjectRuntimeBundle.ShouldBeTrue();
    }

    [Fact]
    public async Task DescribeIntentAsync_ScriptBodyFromActionProperties()
    {
        var ctx = CreateContext(scriptBody: "kubectl apply -f deployment.yaml");

        var intent = (RunScriptIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ScriptBody.ShouldBe("kubectl apply -f deployment.yaml");
    }

    [Fact]
    public async Task DescribeIntentAsync_MissingScriptBody_EmitsEmptyString()
    {
        var action = new DeploymentActionDto
        {
            Name = "no-script",
            ActionType = SpecialVariables.ActionTypes.Script,
            Properties = new List<DeploymentActionPropertyDto>()
        };
        var ctx = new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = "Empty" },
            Action = action
        };

        var intent = (RunScriptIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ScriptBody.ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("Bash", ScriptSyntax.Bash)]
    [InlineData("PowerShell", ScriptSyntax.PowerShell)]
    [InlineData("Python", ScriptSyntax.Python)]
    [InlineData("CSharp", ScriptSyntax.CSharp)]
    [InlineData("FSharp", ScriptSyntax.FSharp)]
    public async Task DescribeIntentAsync_Syntax_ResolvedFromProperties(string input, ScriptSyntax expected)
    {
        var ctx = CreateContext(syntax: input);

        var intent = (RunScriptIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Syntax.ShouldBe(expected);
    }

    [Fact]
    public async Task DescribeIntentAsync_DefaultsToBashWhenSyntaxMissing()
    {
        var ctx = CreateContext();

        var intent = (RunScriptIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task DescribeIntentAsync_PopulatesStepNameAndActionName()
    {
        var ctx = CreateContext(stepName: "Deploy To Production", actionName: "deploy-web");

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Deploy To Production");
        intent.ActionName.ShouldBe("deploy-web");
    }

    [Fact]
    public async Task DescribeIntentAsync_NullStep_EmitsEmptyStepName()
    {
        var ctx = new ActionExecutionContext
        {
            Step = null,
            Action = CreateAction()
        };

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task DescribeIntentAsync_DoesNotUseLegacyName()
    {
        // The adapter path would produce "legacy:Squid.Script" — the override must not go through it.
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldNotStartWith("legacy:");
    }

    [Fact]
    public async Task DescribeIntentAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task DescribeIntentAsync_EmptyAssetsAndPackages()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Assets.ShouldBeEmpty();
        intent.Packages.ShouldBeEmpty();
    }
}
