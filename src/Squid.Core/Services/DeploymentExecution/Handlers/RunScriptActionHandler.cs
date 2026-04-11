using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Handlers;

public class RunScriptActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.Script;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var userScript = ctx.Action.GetProperty(SpecialVariables.Action.ScriptBody) ?? string.Empty;
        var syntax = ScriptSyntaxHelper.ResolveSyntax(ctx.Action);

        var result = new ActionExecutionResult
        {
            ScriptBody = userScript,
            CalamariCommand = null,
            ExecutionMode = ExecutionMode.DirectScript,
            ContextPreparationPolicy = ContextPreparationPolicy.Apply,
            PayloadKind = PayloadKind.None,
            Syntax = syntax
        };

        return Task.FromResult(result);
    }

    /// <summary>
    /// Phase 9b — direct intent emission. Bypasses <see cref="PrepareAsync"/> entirely and
    /// produces a <see cref="RunScriptIntent"/> with <c>InjectRuntimeBundle = true</c> so the
    /// Phase 8 runtime bundle (<c>set_squidvariable</c>, <c>new_squidartifact</c>,
    /// <c>fail_step</c>) is always injected by the renderer.
    /// </summary>
    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var scriptBody = ctx.Action?.GetProperty(SpecialVariables.Action.ScriptBody) ?? string.Empty;
        var syntax = ScriptSyntaxHelper.ResolveSyntax(ctx.Action);

        var intent = new RunScriptIntent
        {
            Name = "run-script",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = ctx.Action?.Name ?? string.Empty,
            ScriptBody = scriptBody,
            Syntax = syntax,
            InjectRuntimeBundle = true
        };

        return Task.FromResult<ExecutionIntent>(intent);
    }
}
