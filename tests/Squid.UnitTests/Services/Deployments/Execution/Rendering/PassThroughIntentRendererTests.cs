using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;
using Squid.Core.Services.DeploymentExecution.OpenClaw.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution.Rendering;

/// <summary>
/// Phase 5 — unit tests for the pass-through renderer base and the concrete subclasses that
/// still behave as pure pass-through (KubernetesAgent, OpenClaw) plus the server variant.
/// Every listed renderer must return <see cref="IntentRenderContext.LegacyRequest"/>
/// unchanged.
///
/// <para>
/// Phase 9i — SSH has been removed from this matrix because
/// <see cref="Squid.Core.Services.DeploymentExecution.Ssh.Rendering.SshIntentRenderer"/>
/// now natively renders <see cref="RunScriptIntent"/> from intent + context.
/// </para>
///
/// <para>
/// Phase 9j.1 — KubernetesApi has been removed from this matrix because
/// <see cref="KubernetesApiIntentRenderer"/> now natively renders
/// <see cref="RunScriptIntent"/> (wrapping the script body with kubectl context) instead of
/// forwarding the legacy request. Later Phase 9j sub-steps will retire the remaining entries.
/// </para>
/// </summary>
public class PassThroughIntentRendererTests
{
    public static IEnumerable<object[]> AllRenderers => new object[][]
    {
        new object[] { new KubernetesAgentIntentRenderer(), CommunicationStyle.KubernetesAgent },
        new object[] { new OpenClawIntentRenderer(), CommunicationStyle.OpenClaw },
        new object[] { new ServerIntentRenderer(), CommunicationStyle.None },
    };

    [Theory]
    [MemberData(nameof(AllRenderers))]
    public void CommunicationStyle_Matches(IIntentRenderer renderer, CommunicationStyle expected)
    {
        renderer.CommunicationStyle.ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(AllRenderers))]
    public void CanRender_AnyIntent_ReturnsTrue(IIntentRenderer renderer, CommunicationStyle _)
    {
        renderer.CanRender(BuildRunScriptIntent()).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(AllRenderers))]
    public void CanRender_NullIntent_ReturnsFalse(IIntentRenderer renderer, CommunicationStyle _)
    {
        renderer.CanRender(null!).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(AllRenderers))]
    public async Task RenderAsync_NullIntent_Throws(IIntentRenderer renderer, CommunicationStyle _)
    {
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await renderer.RenderAsync(null!, BuildContext(new ScriptExecutionRequest()), CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(AllRenderers))]
    public async Task RenderAsync_NullContext_Throws(IIntentRenderer renderer, CommunicationStyle _)
    {
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await renderer.RenderAsync(BuildRunScriptIntent(), null!, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(AllRenderers))]
    public async Task RenderAsync_NullLegacyRequest_ThrowsIntentRenderingException(IIntentRenderer renderer, CommunicationStyle style)
    {
        var ctx = BuildContext(legacyRequest: null);
        var intent = BuildRunScriptIntent();

        var ex = await Should.ThrowAsync<IntentRenderingException>(
            async () => await renderer.RenderAsync(intent, ctx, CancellationToken.None));

        ex.CommunicationStyle.ShouldBe(style);
        ex.IntentName.ShouldBe(intent.Name);
    }

    [Theory]
    [MemberData(nameof(AllRenderers))]
    public async Task RenderAsync_ReturnsLegacyRequestUnchanged(IIntentRenderer renderer, CommunicationStyle _)
    {
        var legacy = new ScriptExecutionRequest { ScriptBody = "echo from legacy" };
        var ctx = BuildContext(legacy);
        var intent = BuildRunScriptIntent();

        var rendered = await renderer.RenderAsync(intent, ctx, CancellationToken.None);

        rendered.ShouldBeSameAs(legacy);
        rendered.ScriptBody.ShouldBe("echo from legacy");
    }

    private static RunScriptIntent BuildRunScriptIntent() => new()
    {
        Name = "run-script",
        ScriptBody = "echo"
    };

    private static IntentRenderContext BuildContext(ScriptExecutionRequest? legacyRequest) => new()
    {
        Target = new DeploymentTargetContext
        {
            Machine = new Machine { Id = 1, Name = "m1" },
            CommunicationStyle = CommunicationStyle.Ssh,
            EndpointContext = new EndpointContext()
        },
        Step = new DeploymentStepDto { Name = "step-1" },
        EffectiveVariables = new List<VariableDto>(),
        ServerTaskId = 42,
        ReleaseVersion = "1.0.0",
        StepTimeout = null,
        LegacyRequest = legacyRequest
    };
}
