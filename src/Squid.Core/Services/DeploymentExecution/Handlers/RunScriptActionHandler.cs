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

    /// <summary>
    /// Produces a <see cref="RunScriptIntent"/> with <c>InjectRuntimeBundle = true</c> so the
    /// runtime bundle (<c>set_squidvariable</c>, <c>new_squidartifact</c>,
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
