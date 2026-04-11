using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Execution.Handlers;

/// <summary>
/// Phase 9a — Verifies the virtual default implementation of
/// <see cref="IActionHandler.DescribeIntentAsync"/>. The default seam MUST:
/// <list type="bullet">
///   <item><description>Invoke <c>PrepareAsync</c> exactly once.</description></item>
///   <item><description>Return the intent subtype matching the action's <c>ActionType</c> via <c>LegacyIntentAdapter</c>.</description></item>
///   <item><description>Forward <c>context.Step?.Name</c> to the adapter as <c>stepName</c>.</description></item>
///   <item><description>Populate missing <c>ActionName</c>/<c>ActionType</c> on the legacy result from <c>context.Action</c>.</description></item>
///   <item><description>Be overridable by explicit interface implementation, bypassing <c>PrepareAsync</c> entirely.</description></item>
///   <item><description>Guard against null context.</description></item>
/// </list>
/// This test exists to prove the seam is in place BEFORE any handler migrates (Phase 9b+).
/// </summary>
public class IActionHandlerDescribeIntentTests
{
    private sealed class StubLegacyHandler : IActionHandler
    {
        public StubLegacyHandler(string actionType = SpecialVariables.ActionTypes.Script)
        {
            ActionType = actionType;
        }

        public string ActionType { get; }

        public int PrepareCallCount { get; private set; }

        public string ScriptBody { get; init; } = "echo legacy-default";

        public bool IncludeFiles { get; init; }

        public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
        {
            PrepareCallCount++;

            var result = new ActionExecutionResult
            {
                ScriptBody = ScriptBody,
                Syntax = ScriptSyntax.Bash
            };

            if (IncludeFiles)
                result.Files = new Dictionary<string, byte[]> { ["deployment.yaml"] = new byte[] { 1, 2, 3 } };

            return Task.FromResult(result);
        }
    }

    private sealed class StubMigratedHandler : IActionHandler
    {
        public string ActionType => SpecialVariables.ActionTypes.Script;

        public int PrepareCallCount { get; private set; }

        public int DescribeIntentCallCount { get; private set; }

        public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
        {
            PrepareCallCount++;

            throw new NotSupportedException("PrepareAsync must not be called when DescribeIntentAsync is overridden.");
        }

        Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
        {
            DescribeIntentCallCount++;

            return Task.FromResult<ExecutionIntent>(new RunScriptIntent
            {
                Name = "run-script",
                StepName = ctx.Step?.Name ?? string.Empty,
                ActionName = ctx.Action?.Name ?? string.Empty,
                ScriptBody = "echo overridden",
                Syntax = ScriptSyntax.Bash,
                InjectRuntimeBundle = true
            });
        }
    }

    private static ActionExecutionContext CreateContext(
        string stepName = "Deploy Web",
        string actionType = SpecialVariables.ActionTypes.Script,
        string actionName = "my-action")
    {
        return new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = stepName },
            Action = new DeploymentActionDto
            {
                Name = actionName,
                ActionType = actionType
            }
        };
    }

    [Fact]
    public async Task DescribeIntentAsync_Default_CallsPrepareAsyncExactlyOnce()
    {
        var handler = new StubLegacyHandler();
        var ctx = CreateContext();

        await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        handler.PrepareCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task DescribeIntentAsync_Default_ForwardsStepNameToAdapter()
    {
        var handler = new StubLegacyHandler();
        var ctx = CreateContext(stepName: "Deploy To Production");

        var intent = await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Deploy To Production");
    }

    [Fact]
    public async Task DescribeIntentAsync_Default_NullStep_PassesEmptyStepName()
    {
        var handler = new StubLegacyHandler();
        var ctx = new ActionExecutionContext
        {
            Step = null,
            Action = new DeploymentActionDto { Name = "solo", ActionType = SpecialVariables.ActionTypes.Script }
        };

        var intent = await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task DescribeIntentAsync_Default_ScriptActionType_ReturnsRunScriptIntent()
    {
        var handler = new StubLegacyHandler(SpecialVariables.ActionTypes.Script)
        {
            ScriptBody = "echo adapter-path"
        };
        var ctx = CreateContext(actionName: "run-web");

        var intent = await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        var run = intent.ShouldBeOfType<RunScriptIntent>();
        run.Name.ShouldBe($"legacy:{SpecialVariables.ActionTypes.Script}");
        run.ActionName.ShouldBe("run-web");
        run.ScriptBody.ShouldBe("echo adapter-path");
        run.Syntax.ShouldBe(ScriptSyntax.Bash);
        run.InjectRuntimeBundle.ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeIntentAsync_Default_HealthCheckActionType_ReturnsHealthCheckIntent()
    {
        var handler = new StubLegacyHandler(SpecialVariables.ActionTypes.HealthCheck)
        {
            ScriptBody = "curl --fail http://svc/health"
        };
        var ctx = CreateContext(actionType: SpecialVariables.ActionTypes.HealthCheck, actionName: "health");

        var intent = await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        var hc = intent.ShouldBeOfType<HealthCheckIntent>();
        hc.CustomScript.ShouldBe("curl --fail http://svc/health");
        hc.ActionName.ShouldBe("health");
    }

    [Fact]
    public async Task DescribeIntentAsync_Default_KubernetesActionType_ReturnsKubernetesApplyIntent()
    {
        var handler = new StubLegacyHandler(SpecialVariables.ActionTypes.KubernetesDeployRawYaml)
        {
            IncludeFiles = true
        };
        var ctx = CreateContext(actionType: SpecialVariables.ActionTypes.KubernetesDeployRawYaml, actionName: "apply-web");

        var intent = await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        var apply = intent.ShouldBeOfType<KubernetesApplyIntent>();
        apply.ActionName.ShouldBe("apply-web");
        apply.YamlFiles.Count.ShouldBe(1);
        apply.YamlFiles[0].RelativePath.ShouldBe("deployment.yaml");
    }

    [Fact]
    public async Task DescribeIntentAsync_Default_HelmActionType_ReturnsHelmUpgradeIntentWithReleaseNameFromActionName()
    {
        var handler = new StubLegacyHandler(SpecialVariables.ActionTypes.HelmChartUpgrade);
        var ctx = CreateContext(actionType: SpecialVariables.ActionTypes.HelmChartUpgrade, actionName: "api-v2");

        var intent = await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        var helm = intent.ShouldBeOfType<HelmUpgradeIntent>();
        helm.ReleaseName.ShouldBe("api-v2");
    }

    [Fact]
    public async Task DescribeIntentAsync_Override_BypassesPrepareAsync()
    {
        var handler = new StubMigratedHandler();
        var ctx = CreateContext(stepName: "Migrated", actionName: "migrated-action");

        var intent = await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        handler.PrepareCallCount.ShouldBe(0);
        handler.DescribeIntentCallCount.ShouldBe(1);

        var run = intent.ShouldBeOfType<RunScriptIntent>();
        run.ScriptBody.ShouldBe("echo overridden");
        run.InjectRuntimeBundle.ShouldBeTrue();
        run.StepName.ShouldBe("Migrated");
        run.ActionName.ShouldBe("migrated-action");
    }

    [Fact]
    public async Task DescribeIntentAsync_Default_NullContext_Throws()
    {
        var handler = new StubLegacyHandler();

        await Should.ThrowAsync<ArgumentNullException>(
            () => ((IActionHandler)handler).DescribeIntentAsync(null!, CancellationToken.None));
    }
}
