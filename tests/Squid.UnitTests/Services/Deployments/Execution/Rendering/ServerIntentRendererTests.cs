using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution.Rendering;

/// <summary>
/// <see cref="ServerIntentRenderer"/> natively renders <see cref="RunScriptIntent"/> for the
/// server transport (<see cref="CommunicationStyle.None"/>). The body is passed through
/// unmodified (no kubectl context, no runtime bundle). Unsupported intents throw
/// <see cref="IntentRenderingException"/>.
/// </summary>
public class ServerIntentRendererTests
{
    private readonly ServerIntentRenderer _renderer = new();

    // ========== Identity / capability checks ==========

    [Fact]
    public void CommunicationStyle_None()
    {
        _renderer.CommunicationStyle.ShouldBe(CommunicationStyle.None);
    }

    [Fact]
    public void CanRender_RunScriptIntent_True()
    {
        _renderer.CanRender(new RunScriptIntent { Name = "run", StepName = "s", ActionName = "a", ScriptBody = "echo 1", Syntax = ScriptSyntax.Bash }).ShouldBeTrue();
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
        var intent = NewRunScriptIntent(scriptBody: "Write-Host from-server");

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.ScriptBody.ShouldBe("Write-Host from-server");
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
        var intent = NewRunScriptIntent() with { StepName = "Server Step", ActionName = "Server Action" };

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.StepName.ShouldBe("Server Step");
        rendered.ActionName.ShouldBe("Server Action");
    }

    // ========== RunScriptIntent: context-sourced fields ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_VariablesFromContext()
    {
        var contextVars = new List<VariableDto>
        {
            new() { Name = "Env", Value = "Production" },
            new() { Name = "Token", Value = "secret", IsSensitive = true }
        };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(variables: contextVars), CancellationToken.None);

        rendered.Variables.Select(v => v.Name).ShouldBe(new[] { "Env", "Token" });
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_MachineAndEndpointFromContext()
    {
        var machine = new Machine { Id = 10, Name = "server-worker" };
        var endpoint = new EndpointContext();
        var target = new DeploymentTargetContext
        {
            Machine = machine,
            EndpointContext = endpoint,
            CommunicationStyle = CommunicationStyle.None
        };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(target: target), CancellationToken.None);

        rendered.Machine.ShouldBeSameAs(machine);
        rendered.EndpointContext.ShouldBeSameAs(endpoint);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ServerTaskIdAndReleaseVersionFromContext()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(serverTaskId: 77, releaseVersion: "3.0.0"), CancellationToken.None);

        rendered.ServerTaskId.ShouldBe(77);
        rendered.ReleaseVersion.ShouldBe("3.0.0");
    }

    // ========== Timeout resolution ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_TimeoutPrefersIntentOverStepTimeout()
    {
        var intent = NewRunScriptIntent() with { Timeout = TimeSpan.FromMinutes(5) };

        var rendered = await _renderer.RenderAsync(intent, NewContext(stepTimeout: TimeSpan.FromMinutes(10)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_TimeoutFallsBackToStepTimeout()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(stepTimeout: TimeSpan.FromMinutes(10)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(10));
    }

    // ========== Execution mode ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ExecutionModeDirectScript()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(), CancellationToken.None);

        rendered.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        rendered.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Apply);
        rendered.PayloadKind.ShouldBe(PayloadKind.None);
    }

    // ========== Files and packages ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_DeploymentFilesAlwaysEmpty()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(), CancellationToken.None);

        rendered.DeploymentFiles.Count.ShouldBe(0);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PackageReferencesFromContext()
    {
        var packages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/pkg.zip", PackageId: "Acme", Version: "1.0.0", SizeBytes: 500, Hash: "hash1")
        };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(packageReferences: packages), CancellationToken.None);

        rendered.PackageReferences.ShouldBe(packages);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PackageReferencesEmptyByDefault()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(), CancellationToken.None);

        rendered.PackageReferences.ShouldBeEmpty();
    }

    // ========== Unsupported intents throw ==========

    [Fact]
    public async Task RenderAsync_UnsupportedIntent_ThrowsIntentRenderingException()
    {
        var intent = new ManualInterventionIntent { Name = "manual-intervention" };

        var ex = await Should.ThrowAsync<IntentRenderingException>(
            async () => await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None));

        ex.CommunicationStyle.ShouldBe(CommunicationStyle.None);
        ex.IntentName.ShouldBe("manual-intervention");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ThrowsIntentRenderingException()
    {
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply", StepName = "s", ActionName = "a",
            Namespace = "default", Syntax = ScriptSyntax.Bash,
            YamlFiles = new List<Squid.Core.Services.DeploymentExecution.Script.Files.DeploymentFile>()
        };

        await Should.ThrowAsync<IntentRenderingException>(
            async () => await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None));
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
            Syntax = syntax
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
                Machine = new Machine { Id = 1, Name = "server-1" },
                CommunicationStyle = CommunicationStyle.None,
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
