using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle.Rendering;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class TentacleIntentRendererTests
{
    [Theory]
    [InlineData(typeof(TentacleListeningIntentRenderer), CommunicationStyle.LinuxListening)]
    [InlineData(typeof(TentaclePollingIntentRenderer), CommunicationStyle.LinuxPolling)]
    public void CommunicationStyle_MatchesExpected(Type rendererType, CommunicationStyle expected)
    {
        var renderer = (IIntentRenderer)Activator.CreateInstance(rendererType);

        renderer.CommunicationStyle.ShouldBe(expected);
    }

    [Theory]
    [InlineData(typeof(TentacleListeningIntentRenderer))]
    [InlineData(typeof(TentaclePollingIntentRenderer))]
    public void CanRender_RunScriptIntent_ReturnsTrue(Type rendererType)
    {
        var renderer = (IIntentRenderer)Activator.CreateInstance(rendererType);

        renderer.CanRender(NewRunScriptIntent()).ShouldBeTrue();
    }

    [Theory]
    [InlineData(typeof(TentacleListeningIntentRenderer))]
    [InlineData(typeof(TentaclePollingIntentRenderer))]
    public async Task RenderAsync_RunScriptIntent_Bash_ProducesCorrectRequest(Type rendererType)
    {
        var renderer = (IIntentRenderer)Activator.CreateInstance(rendererType);

        var rendered = await renderer.RenderAsync(NewRunScriptIntent(scriptBody: "echo hello"), NewContext(), CancellationToken.None);

        rendered.ScriptBody.ShouldBe("echo hello");
        rendered.Syntax.ShouldBe(ScriptSyntax.Bash);
        rendered.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
    }

    [Theory]
    [InlineData(typeof(TentacleListeningIntentRenderer))]
    [InlineData(typeof(TentaclePollingIntentRenderer))]
    public async Task RenderAsync_RunScriptIntent_PowerShell_ProducesCorrectRequest(Type rendererType)
    {
        var renderer = (IIntentRenderer)Activator.CreateInstance(rendererType);

        var rendered = await renderer.RenderAsync(NewRunScriptIntent(syntax: ScriptSyntax.PowerShell), NewContext(), CancellationToken.None);

        rendered.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Theory]
    [InlineData(typeof(TentacleListeningIntentRenderer))]
    [InlineData(typeof(TentaclePollingIntentRenderer))]
    public async Task RenderAsync_RunScriptIntent_UsesIntentTimeout_WhenSet(Type rendererType)
    {
        var renderer = (IIntentRenderer)Activator.CreateInstance(rendererType);
        var intent = NewRunScriptIntent() with { Timeout = TimeSpan.FromMinutes(5) };

        var rendered = await renderer.RenderAsync(intent, NewContext(stepTimeout: TimeSpan.FromMinutes(30)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData(typeof(TentacleListeningIntentRenderer))]
    [InlineData(typeof(TentaclePollingIntentRenderer))]
    public async Task RenderAsync_RunScriptIntent_FallsBackToStepTimeout(Type rendererType)
    {
        var renderer = (IIntentRenderer)Activator.CreateInstance(rendererType);

        var rendered = await renderer.RenderAsync(NewRunScriptIntent(), NewContext(stepTimeout: TimeSpan.FromMinutes(15)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData(typeof(TentacleListeningIntentRenderer))]
    [InlineData(typeof(TentaclePollingIntentRenderer))]
    public async Task RenderAsync_RunScriptIntent_StepAndActionNameFromIntent(Type rendererType)
    {
        var renderer = (IIntentRenderer)Activator.CreateInstance(rendererType);
        var intent = NewRunScriptIntent() with { StepName = "Deploy Step", ActionName = "Deploy Action" };

        var rendered = await renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.StepName.ShouldBe("Deploy Step");
        rendered.ActionName.ShouldBe("Deploy Action");
    }

    [Theory]
    [InlineData(typeof(TentacleListeningIntentRenderer))]
    [InlineData(typeof(TentaclePollingIntentRenderer))]
    public async Task RenderAsync_RunScriptIntent_MachineFromContext(Type rendererType)
    {
        var renderer = (IIntentRenderer)Activator.CreateInstance(rendererType);
        var machine = new Machine { Id = 7, Name = "linux-box" };
        var target = new DeploymentTargetContext
        {
            Machine = machine,
            EndpointContext = new EndpointContext(),
            CommunicationStyle = CommunicationStyle.LinuxListening
        };

        var rendered = await renderer.RenderAsync(NewRunScriptIntent(), NewContext(target: target), CancellationToken.None);

        rendered.Machine.ShouldBeSameAs(machine);
    }

    [Theory]
    [InlineData(typeof(TentacleListeningIntentRenderer))]
    [InlineData(typeof(TentaclePollingIntentRenderer))]
    public async Task RenderAsync_NonRunScriptIntent_Throws(Type rendererType)
    {
        var renderer = (IIntentRenderer)Activator.CreateInstance(rendererType);
        var intent = new KubernetesApplyIntent { Name = "k8s-apply", StepName = "S", ActionName = "A", YamlFiles = Array.Empty<Squid.Core.Services.DeploymentExecution.Script.Files.DeploymentFile>() };

        await Should.ThrowAsync<IntentRenderingException>(
            () => renderer.RenderAsync(intent, NewContext(), CancellationToken.None));
    }

    private static RunScriptIntent NewRunScriptIntent(string scriptBody = "echo default", ScriptSyntax syntax = ScriptSyntax.Bash)
    {
        return new RunScriptIntent
        {
            Name = "run-script",
            StepName = "step-1",
            ActionName = "action-1",
            ScriptBody = scriptBody,
            Syntax = syntax
        };
    }

    private static IntentRenderContext NewContext(
        DeploymentTargetContext target = null,
        TimeSpan? stepTimeout = null)
    {
        return new IntentRenderContext
        {
            Target = target ?? new DeploymentTargetContext
            {
                Machine = new Machine { Id = 1, Name = "test-machine" },
                CommunicationStyle = CommunicationStyle.LinuxListening,
                EndpointContext = new EndpointContext()
            },
            Step = new DeploymentStepDto { Name = "step-1" },
            EffectiveVariables = new List<VariableDto>(),
            ServerTaskId = 42,
            ReleaseVersion = "1.0.0",
            StepTimeout = stepTimeout
        };
    }
}
