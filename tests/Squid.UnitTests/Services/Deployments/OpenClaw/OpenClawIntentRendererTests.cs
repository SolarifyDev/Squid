using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.OpenClaw.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.OpenClaw;

/// <summary>
/// Phase 9j.4 — <see cref="OpenClawIntentRenderer"/> natively renders
/// <see cref="OpenClawInvokeIntent"/> by constructing a fresh
/// <see cref="ScriptExecutionRequest"/> from the intent plus <see cref="IntentRenderContext"/>.
/// The renderer maps <see cref="OpenClawInvokeIntent.Kind"/> onto the legacy
/// <c>ScriptExecutionRequest.ActionType</c> string expected by
/// <c>OpenClawExecutionStrategy</c>, forwards <see cref="OpenClawInvokeIntent.Parameters"/>
/// as <c>ActionProperties</c>, and hydrates variables / machine / timeout from the render
/// context. Non-native intents still fall back to the Phase 5 pass-through path
/// (<see cref="IntentRenderContext.LegacyRequest"/>) and throw
/// <see cref="IntentRenderingException"/> when it is absent.
/// </summary>
public class OpenClawIntentRendererTests
{
    private readonly OpenClawIntentRenderer _renderer = new();

    // ========== Identity / capability checks ==========

    [Fact]
    public void CommunicationStyle_OpenClaw()
    {
        _renderer.CommunicationStyle.ShouldBe(CommunicationStyle.OpenClaw);
    }

    [Fact]
    public void CanRender_OpenClawInvokeIntent_True()
    {
        _renderer.CanRender(NewInvokeIntent()).ShouldBeTrue();
    }

    [Fact]
    public void CanRender_RunScriptIntent_True()
    {
        _renderer.CanRender(new RunScriptIntent { Name = "run-script", ScriptBody = "echo" }).ShouldBeTrue();
    }

    [Fact]
    public void CanRender_Null_False()
    {
        _renderer.CanRender(null!).ShouldBeFalse();
    }

    // ========== Guard clauses ==========

    [Fact]
    public async Task RenderAsync_NullIntent_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await _renderer.RenderAsync(null!, NewContext(legacy: new ScriptExecutionRequest()), CancellationToken.None));
    }

    [Fact]
    public async Task RenderAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await _renderer.RenderAsync(NewInvokeIntent(), null!, CancellationToken.None));
    }

    // ========== Kind → ActionType mapping ==========

    [Theory]
    [InlineData(OpenClawInvocationKind.Wake, SpecialVariables.ActionTypes.OpenClawWake)]
    [InlineData(OpenClawInvocationKind.Assert, SpecialVariables.ActionTypes.OpenClawAssert)]
    [InlineData(OpenClawInvocationKind.ChatCompletion, SpecialVariables.ActionTypes.OpenClawChatCompletion)]
    [InlineData(OpenClawInvocationKind.FetchResult, SpecialVariables.ActionTypes.OpenClawFetchResult)]
    [InlineData(OpenClawInvocationKind.InvokeTool, SpecialVariables.ActionTypes.OpenClawInvokeTool)]
    [InlineData(OpenClawInvocationKind.RunAgent, SpecialVariables.ActionTypes.OpenClawRunAgent)]
    [InlineData(OpenClawInvocationKind.WaitSession, SpecialVariables.ActionTypes.OpenClawWaitSession)]
    public async Task RenderAsync_InvokeIntent_KindMapsToActionType(OpenClawInvocationKind kind, string expectedActionType)
    {
        var intent = NewInvokeIntent(kind: kind);

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ActionType.ShouldBe(expectedActionType);
    }

    // ========== Parameters → ActionProperties ==========

    [Fact]
    public async Task RenderAsync_InvokeIntent_ParametersCopiedToActionProperties()
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SpecialVariables.OpenClaw.PropTool] = "kubectl",
            [SpecialVariables.OpenClaw.PropToolAction] = "apply",
            [SpecialVariables.OpenClaw.PropArgsJson] = """{"file":"deploy.yaml"}"""
        };
        var intent = NewInvokeIntent(kind: OpenClawInvocationKind.InvokeTool, parameters: parameters);

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ActionProperties.ShouldNotBeNull();
        rendered.ActionProperties[SpecialVariables.OpenClaw.PropTool].ShouldBe("kubectl");
        rendered.ActionProperties[SpecialVariables.OpenClaw.PropToolAction].ShouldBe("apply");
        rendered.ActionProperties[SpecialVariables.OpenClaw.PropArgsJson].ShouldBe("""{"file":"deploy.yaml"}""");
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_EmptyParameters_ProducesEmptyActionProperties()
    {
        var intent = NewInvokeIntent(parameters: new Dictionary<string, string>());

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ActionProperties.ShouldNotBeNull();
        rendered.ActionProperties.Count.ShouldBe(0);
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_ActionPropertiesNotNull_WhenIntentParametersNull()
    {
        var intent = new OpenClawInvokeIntent
        {
            Name = "openclaw-invoke-tool",
            Kind = OpenClawInvocationKind.InvokeTool
            // Parameters defaults to empty dictionary via record init
        };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ActionProperties.ShouldNotBeNull();
    }

    // ========== Context fields flowing through ==========

    [Fact]
    public async Task RenderAsync_InvokeIntent_VariablesFromContext()
    {
        var vars = new List<VariableDto> { new() { Name = SpecialVariables.OpenClaw.BaseUrl, Value = "https://gateway" } };

        var rendered = await _renderer.RenderAsync(NewInvokeIntent(), NewContext(legacy: null, variables: vars), CancellationToken.None);

        rendered.Variables.ShouldNotBeNull();
        rendered.Variables.Select(v => v.Name).ShouldContain(SpecialVariables.OpenClaw.BaseUrl);
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_MachineAndEndpointFromContext()
    {
        var machine = new Machine { Id = 9, Name = "openclaw-worker" };
        var endpoint = new EndpointContext { EndpointJson = "{}" };
        var target = new DeploymentTargetContext
        {
            Machine = machine,
            EndpointContext = endpoint,
            CommunicationStyle = CommunicationStyle.OpenClaw
        };

        var rendered = await _renderer.RenderAsync(NewInvokeIntent(), NewContext(legacy: null, target: target), CancellationToken.None);

        rendered.Machine.ShouldBeSameAs(machine);
        rendered.EndpointContext.ShouldBeSameAs(endpoint);
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_ServerTaskIdAndReleaseVersionFromContext()
    {
        var rendered = await _renderer.RenderAsync(
            NewInvokeIntent(),
            NewContext(legacy: null, serverTaskId: 99, releaseVersion: "2.5.0"),
            CancellationToken.None);

        rendered.ServerTaskId.ShouldBe(99);
        rendered.ReleaseVersion.ShouldBe("2.5.0");
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_StepAndActionNameFromIntent()
    {
        var intent = NewInvokeIntent() with { StepName = "Wake Prod", ActionName = "Wake Action" };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.StepName.ShouldBe("Wake Prod");
        rendered.ActionName.ShouldBe("Wake Action");
    }

    // ========== Timeout resolution ==========

    [Fact]
    public async Task RenderAsync_InvokeIntent_TimeoutPrefersIntentOverStepTimeout()
    {
        var intent = NewInvokeIntent() with { Timeout = TimeSpan.FromMinutes(3) };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null, stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_TimeoutFallsBackToStepTimeout()
    {
        var rendered = await _renderer.RenderAsync(NewInvokeIntent(), NewContext(legacy: null, stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_NoTimeoutAnywhere_IsNull()
    {
        var rendered = await _renderer.RenderAsync(NewInvokeIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.Timeout.ShouldBeNull();
    }

    // ========== Execution mode / syntax defaults ==========

    [Fact]
    public async Task RenderAsync_InvokeIntent_ExecutionModeDirectScript()
    {
        var rendered = await _renderer.RenderAsync(NewInvokeIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_ContextPreparationPolicySkip()
    {
        var rendered = await _renderer.RenderAsync(NewInvokeIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Skip);
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_PayloadKindNone()
    {
        var rendered = await _renderer.RenderAsync(NewInvokeIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.PayloadKind.ShouldBe(PayloadKind.None);
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_SyntaxBash()
    {
        var rendered = await _renderer.RenderAsync(NewInvokeIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_FilesEmpty()
    {
        var rendered = await _renderer.RenderAsync(NewInvokeIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.Files.ShouldNotBeNull();
        rendered.Files.Count.ShouldBe(0);
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_PackageReferencesEmpty()
    {
        var rendered = await _renderer.RenderAsync(NewInvokeIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.PackageReferences.ShouldNotBeNull();
        rendered.PackageReferences.Count.ShouldBe(0);
    }

    [Fact]
    public async Task RenderAsync_InvokeIntent_IgnoresLegacyRequestFields()
    {
        var legacy = new ScriptExecutionRequest
        {
            ScriptBody = "legacy body",
            ActionType = SpecialVariables.ActionTypes.Script,
            ActionProperties = new Dictionary<string, string> { ["stale"] = "value" }
        };
        var intent = NewInvokeIntent(kind: OpenClawInvocationKind.Wake);

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy), CancellationToken.None);

        rendered.ShouldNotBeSameAs(legacy);
        rendered.ActionType.ShouldBe(SpecialVariables.ActionTypes.OpenClawWake);
        rendered.ActionProperties.ShouldNotContainKey("stale");
    }

    // ========== Unsupported intents throw ==========

    [Fact]
    public async Task RenderAsync_UnsupportedIntent_ThrowsIntentRenderingException()
    {
        var intent = new ManualInterventionIntent { Name = "manual-intervention" };

        var ex = await Should.ThrowAsync<IntentRenderingException>(
            async () => await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None));

        ex.CommunicationStyle.ShouldBe(CommunicationStyle.OpenClaw);
        ex.IntentName.ShouldBe("manual-intervention");
    }

    // ========== Helpers ==========

    private static OpenClawInvokeIntent NewInvokeIntent(
        OpenClawInvocationKind kind = OpenClawInvocationKind.InvokeTool,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        return new OpenClawInvokeIntent
        {
            Name = $"openclaw-{kind}",
            StepName = "step-1",
            ActionName = "action-1",
            Kind = kind,
            Parameters = parameters ?? new Dictionary<string, string>()
        };
    }

    private static IntentRenderContext NewContext(
        ScriptExecutionRequest? legacy,
        List<VariableDto>? variables = null,
        DeploymentTargetContext? target = null,
        int serverTaskId = 42,
        string? releaseVersion = "1.0.0",
        TimeSpan? stepTimeout = null)
    {
        return new IntentRenderContext
        {
            Target = target ?? new DeploymentTargetContext
            {
                Machine = new Machine { Id = 1, Name = "m1" },
                CommunicationStyle = CommunicationStyle.OpenClaw,
                EndpointContext = new EndpointContext()
            },
            Step = new DeploymentStepDto { Name = "step-1" },
            EffectiveVariables = variables ?? new List<VariableDto>(),
            ServerTaskId = serverTaskId,
            ReleaseVersion = releaseVersion,
            StepTimeout = stepTimeout,
            LegacyRequest = legacy
        };
    }
}
