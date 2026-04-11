using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution.Rendering;

/// <summary>
/// Phase 5 — unit tests for the pass-through renderer base and the only remaining concrete
/// subclass that still behaves as pure pass-through: <see cref="ServerIntentRenderer"/>
/// (<see cref="CommunicationStyle.None"/>). It must return
/// <see cref="IntentRenderContext.LegacyRequest"/> unchanged.
///
/// <para>
/// Phase 9i — SSH has been removed from this matrix because
/// <see cref="Squid.Core.Services.DeploymentExecution.Ssh.Rendering.SshIntentRenderer"/>
/// now natively renders <see cref="RunScriptIntent"/> from intent + context.
/// </para>
///
/// <para>
/// Phase 9j.1 — KubernetesApi has been removed from this matrix because
/// <see cref="Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering.KubernetesApiIntentRenderer"/>
/// now natively renders <see cref="RunScriptIntent"/> (wrapping the script body with kubectl
/// context) instead of forwarding the legacy request.
/// </para>
///
/// <para>
/// Phase 9j.3 — KubernetesAgent has been removed from this matrix because
/// <see cref="Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering.KubernetesAgentIntentRenderer"/>
/// now natively renders <see cref="RunScriptIntent"/> and
/// <see cref="KubernetesApplyIntent"/>, wrapping the script body with the
/// <c>kubectl config set-context</c> namespace preamble for shell syntaxes.
/// </para>
///
/// <para>
/// Phase 9j.4 — OpenClaw has been removed from this matrix because
/// <see cref="Squid.Core.Services.DeploymentExecution.OpenClaw.Rendering.OpenClawIntentRenderer"/>
/// now natively renders <see cref="OpenClawInvokeIntent"/>, mapping
/// <see cref="OpenClawInvocationKind"/> onto the legacy <c>ActionType</c> string the
/// OpenClaw execution strategy dispatches on.
/// </para>
/// </summary>
public class PassThroughIntentRendererTests
{
    public static IEnumerable<object[]> AllRenderers => new object[][]
    {
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
