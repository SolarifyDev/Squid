using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Ssh.Rendering;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Ssh;

/// <summary>
/// Phase 9i — <see cref="SshIntentRenderer"/> is no longer a pure pass-through. When it
/// sees a <see cref="RunScriptIntent"/>, it constructs a fresh <see cref="ScriptExecutionRequest"/>
/// from the intent plus <see cref="IntentRenderContext"/>, sourcing script body, syntax,
/// step/action framing, variables, and target from semantic inputs rather than the legacy
/// request. For any other intent kind it still falls through to the pass-through path
/// (until Phase 9j lands transport-native renderers for the remaining intents).
/// </summary>
public class SshIntentRendererTests
{
    private readonly SshIntentRenderer _renderer = new();

    // ========== Identity / capability checks ==========

    [Fact]
    public void CommunicationStyle_Ssh()
    {
        _renderer.CommunicationStyle.ShouldBe(CommunicationStyle.Ssh);
    }

    [Fact]
    public void CanRender_RunScriptIntent_True()
    {
        _renderer.CanRender(NewRunScriptIntent()).ShouldBeTrue();
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
            async () => await _renderer.RenderAsync(null!, NewContext(), CancellationToken.None));
    }

    [Fact]
    public async Task RenderAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await _renderer.RenderAsync(NewRunScriptIntent(), null!, CancellationToken.None));
    }

    // ========== RunScriptIntent: intent-sourced fields ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ScriptBodyFromIntent()
    {
        var intent = NewRunScriptIntent(scriptBody: "echo from-intent");

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.ScriptBody.ShouldBe("echo from-intent");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_SyntaxFromIntent()
    {
        var intent = NewRunScriptIntent(syntax: ScriptSyntax.PowerShell);

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_StepAndActionNameFromIntent()
    {
        var intent = NewRunScriptIntent() with { StepName = "Deploy Step", ActionName = "Deploy Action" };

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.StepName.ShouldBe("Deploy Step");
        rendered.ActionName.ShouldBe("Deploy Action");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_VariablesFromContext()
    {
        var contextVariables = new List<VariableDto>
        {
            new() { Name = "Foo", Value = "Bar" },
            new() { Name = "Secret", Value = "shh", IsSensitive = true }
        };
        var ctx = NewContext(variables: contextVariables);

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), ctx, CancellationToken.None);

        rendered.Variables.ShouldNotBeNull();
        rendered.Variables.Select(v => v.Name).ShouldBe(new[] { "Foo", "Secret" });
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_MachineAndEndpointFromContext()
    {
        var machine = new Machine { Id = 7, Name = "deploy-box" };
        var endpoint = new EndpointContext { EndpointJson = "{ \"Host\": \"deploy-box.internal\" }" };
        var target = new DeploymentTargetContext
        {
            Machine = machine,
            EndpointContext = endpoint,
            CommunicationStyle = CommunicationStyle.Ssh
        };
        var ctx = NewContext(target: target);

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), ctx, CancellationToken.None);

        rendered.Machine.ShouldBeSameAs(machine);
        rendered.EndpointContext.ShouldBeSameAs(endpoint);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ServerTaskIdAndReleaseVersionFromContext()
    {
        var ctx = NewContext(serverTaskId: 99, releaseVersion: "2.5.0");

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), ctx, CancellationToken.None);

        rendered.ServerTaskId.ShouldBe(99);
        rendered.ReleaseVersion.ShouldBe("2.5.0");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_TimeoutPrefersIntentOverStepTimeout()
    {
        var intent = NewRunScriptIntent() with { Timeout = TimeSpan.FromMinutes(3) };
        var ctx = NewContext(stepTimeout: TimeSpan.FromMinutes(7));

        var rendered = await _renderer.RenderAsync(intent, ctx, CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_TimeoutFallsBackToStepTimeout()
    {
        var ctx = NewContext(stepTimeout: TimeSpan.FromMinutes(7));

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), ctx, CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ExecutionModeDirectScript()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(), CancellationToken.None);

        rendered.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        rendered.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Apply);
        rendered.PayloadKind.ShouldBe(PayloadKind.None);
    }

    // ========== RunScriptIntent: native rendering ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_NativeRendering_DoesNotThrow()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(scriptBody: "echo independent"), NewContext(), CancellationToken.None);

        rendered.ShouldNotBeNull();
        rendered.ScriptBody.ShouldBe("echo independent");
        rendered.Files.ShouldNotBeNull();
        rendered.PackageReferences.ShouldNotBeNull();
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PackageReferencesEmptyByDefault()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(), CancellationToken.None);

        rendered.PackageReferences.ShouldBeEmpty();
    }

    // ========== RunScriptIntent: context-sourced PackageReferences ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PackageReferencesFromContext()
    {
        var packages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/acme.zip", PackageId: "Acme.Web", Version: "1.0.0", SizeBytes: 123, Hash: "abc")
        };
        var ctx = NewContext(packageReferences: packages);

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), ctx, CancellationToken.None);

        rendered.PackageReferences.ShouldBe(packages);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PackageReferencesFromContextOnly()
    {
        var contextPackages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/new.zip", PackageId: "New", Version: "2.0.0", SizeBytes: 200, Hash: "new")
        };
        var ctx = NewContext(packageReferences: contextPackages);

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), ctx, CancellationToken.None);

        rendered.PackageReferences.ShouldBe(contextPackages);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_FilesAlwaysEmpty()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(), CancellationToken.None);

        rendered.Files.ShouldBeEmpty();
    }

    // ========== Unsupported intents throw ==========

    [Fact]
    public async Task RenderAsync_UnsupportedIntent_ThrowsIntentRenderingException()
    {
        var intent = new ManualInterventionIntent { Name = "manual-intervention" };

        var ex = await Should.ThrowAsync<IntentRenderingException>(
            async () => await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None));

        ex.CommunicationStyle.ShouldBe(CommunicationStyle.Ssh);
        ex.IntentName.ShouldBe("manual-intervention");
    }

    // ========== Helpers ==========

    private static RunScriptIntent NewRunScriptIntent(string scriptBody = "echo default", ScriptSyntax syntax = ScriptSyntax.Bash)
    {
        return new RunScriptIntent
        {
            Name = "run-script",
            StepName = "step-1",
            ActionName = "action-1",
            ScriptBody = scriptBody,
            Syntax = syntax,
            InjectRuntimeBundle = true
        };
    }

    private static IntentRenderContext NewContext(
        List<VariableDto>? variables = null,
        DeploymentTargetContext? target = null,
        int serverTaskId = 42,
        string? releaseVersion = "1.0.0",
        TimeSpan? stepTimeout = null,
        List<PackageAcquisitionResult>? packageReferences = null)
    {
        return new IntentRenderContext
        {
            Target = target ?? new DeploymentTargetContext
            {
                Machine = new Machine { Id = 1, Name = "m1" },
                CommunicationStyle = CommunicationStyle.Ssh,
                EndpointContext = new EndpointContext()
            },
            Step = new DeploymentStepDto { Name = "step-1" },
            EffectiveVariables = variables ?? new List<VariableDto>(),
            ServerTaskId = serverTaskId,
            ReleaseVersion = releaseVersion,
            StepTimeout = stepTimeout,
            PackageReferences = packageReferences ?? new List<PackageAcquisitionResult>()
        };
    }
}
