using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Deployments.Execution.Rendering;

/// <summary>
/// Phase 5 — unit tests for <see cref="IntentRendererRegistry"/>. Verifies per-transport
/// resolution, TryResolve/Resolve contract, and <see cref="UnsupportedIntentException"/>
/// behaviour.
/// </summary>
public class IntentRendererRegistryTests
{
    [Fact]
    public void Resolve_NoRenderersRegistered_Throws()
    {
        var registry = new IntentRendererRegistry(Array.Empty<IIntentRenderer>());
        var intent = BuildRunScriptIntent();

        Should.Throw<UnsupportedIntentException>(() => registry.Resolve(CommunicationStyle.Ssh, intent));
    }

    [Fact]
    public void TryResolve_NoRenderersRegistered_ReturnsNull()
    {
        var registry = new IntentRendererRegistry(Array.Empty<IIntentRenderer>());
        var intent = BuildRunScriptIntent();

        registry.TryResolve(CommunicationStyle.Ssh, intent).ShouldBeNull();
    }

    [Fact]
    public void Resolve_MatchingStyle_ReturnsRenderer()
    {
        var sshRenderer = new FakeRenderer(CommunicationStyle.Ssh);
        var k8sRenderer = new FakeRenderer(CommunicationStyle.KubernetesApi);
        var registry = new IntentRendererRegistry(new IIntentRenderer[] { sshRenderer, k8sRenderer });

        var resolved = registry.Resolve(CommunicationStyle.KubernetesApi, BuildRunScriptIntent());

        resolved.ShouldBeSameAs(k8sRenderer);
    }

    [Fact]
    public void Resolve_DifferentStyle_Throws()
    {
        var sshRenderer = new FakeRenderer(CommunicationStyle.Ssh);
        var registry = new IntentRendererRegistry(new IIntentRenderer[] { sshRenderer });

        var ex = Should.Throw<UnsupportedIntentException>(
            () => registry.Resolve(CommunicationStyle.KubernetesApi, BuildRunScriptIntent()));

        ex.CommunicationStyle.ShouldBe(CommunicationStyle.KubernetesApi);
        ex.IntentName.ShouldBe("run-script");
    }

    [Fact]
    public void Resolve_MultipleRenderersForSameStyle_PicksFirstThatCanRender()
    {
        var reject = new FakeRenderer(CommunicationStyle.Ssh, canRender: false);
        var accept = new FakeRenderer(CommunicationStyle.Ssh, canRender: true);
        var registry = new IntentRendererRegistry(new IIntentRenderer[] { reject, accept });

        var resolved = registry.Resolve(CommunicationStyle.Ssh, BuildRunScriptIntent());

        resolved.ShouldBeSameAs(accept);
    }

    [Fact]
    public void Resolve_MultipleRenderersNoneAccept_Throws()
    {
        var r1 = new FakeRenderer(CommunicationStyle.Ssh, canRender: false);
        var r2 = new FakeRenderer(CommunicationStyle.Ssh, canRender: false);
        var registry = new IntentRendererRegistry(new IIntentRenderer[] { r1, r2 });

        Should.Throw<UnsupportedIntentException>(
            () => registry.Resolve(CommunicationStyle.Ssh, BuildRunScriptIntent()));
    }

    private static RunScriptIntent BuildRunScriptIntent() => new()
    {
        Name = "run-script",
        ScriptBody = "echo"
    };

    private sealed class FakeRenderer(CommunicationStyle style, bool canRender = true) : IIntentRenderer
    {
        public CommunicationStyle CommunicationStyle { get; } = style;
        public bool CanRender(ExecutionIntent intent) => canRender;

        public Task<ScriptExecutionRequest> RenderAsync(ExecutionIntent intent, IntentRenderContext context, CancellationToken ct)
            => Task.FromResult(new ScriptExecutionRequest());
    }
}
