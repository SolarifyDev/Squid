using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Services.Deployments.Execution.Handlers;

/// <summary>
/// Shared helper for tests that use <see cref="Mock{IActionHandler}"/> and drive the
/// <c>ExecuteStepsPhase</c>. After Phase 9h, the pipeline calls
/// <see cref="IActionHandler.DescribeIntentAsync"/> directly — Moq does not forward
/// unconfigured calls to the interface's default implementation, so tests that previously
/// only set up <c>PrepareAsync</c> now also need to set up <c>DescribeIntentAsync</c>.
/// This helper installs a minimal <see cref="RunScriptIntent"/> that mirrors the legacy
/// adapter's output — enough for pass-through renderers to route the request through
/// unchanged.
/// </summary>
internal static class MockActionHandlerSetup
{
    public static void SetupDescribeIntentAsRunScript(this Mock<IActionHandler> handler, string scriptBody = "echo test", ScriptSyntax syntax = ScriptSyntax.Bash)
    {
        handler
            .Setup(h => h.DescribeIntentAsync(It.IsAny<ActionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActionExecutionContext ctx, CancellationToken _) => new RunScriptIntent
            {
                Name = "run-script",
                StepName = ctx.Step?.Name ?? string.Empty,
                ActionName = ctx.Action?.Name ?? string.Empty,
                ScriptBody = scriptBody,
                Syntax = syntax,
                InjectRuntimeBundle = false
            });
    }
}
