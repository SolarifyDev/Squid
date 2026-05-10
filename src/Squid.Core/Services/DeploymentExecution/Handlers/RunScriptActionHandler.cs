using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Exceptions;
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
    ///
    /// <para><b>P0-1 fix</b> — validates that <see cref="SpecialVariables.Action.ScriptBody"/>
    /// is non-blank. The pre-fix behaviour silently emitted an empty intent and let the shell
    /// execute a no-op script (<c>set -e</c> immediate exit), reporting success. That hid
    /// misconfigured steps where an operator forgot to fill in the script — the deploy was
    /// "✅ green" while doing nothing, and downstream steps that read output variables saw
    /// stale / missing values and produced subtle bugs in production.</para>
    ///
    /// <para>The planner stub at <c>DeploymentPlanner.BuildStubIntent</c> intentionally uses
    /// an empty body and is NOT routed through this method — it only goes through capability
    /// validation, never execution.</para>
    /// </summary>
    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var scriptBody = ctx.Action?.GetProperty(SpecialVariables.Action.ScriptBody);

        if (string.IsNullOrWhiteSpace(scriptBody))
            throw new DeploymentValidationException(
                $"RunScript action '{ctx.Action?.Name ?? "(unknown)"}' in step '{ctx.Step?.Name ?? "(unknown)"}' " +
                $"has an empty or whitespace-only ScriptBody. " +
                $"This usually means the step's script field was left blank during configuration. " +
                $"Set the '{SpecialVariables.Action.ScriptBody}' property to a non-empty script, " +
                $"or remove this action / disable this step if it is no longer needed.");

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
