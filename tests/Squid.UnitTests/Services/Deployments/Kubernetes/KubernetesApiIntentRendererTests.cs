using System.Linq;
using System.Text.Json;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Phase 9j.1 — <see cref="KubernetesApiIntentRenderer"/> is no longer a pure pass-through.
/// When it sees a <see cref="RunScriptIntent"/>, it constructs a fresh
/// <see cref="ScriptExecutionRequest"/> from the intent plus <see cref="IntentRenderContext"/>,
/// applying kubectl context wrapping via <see cref="IKubernetesApiContextScriptBuilder"/> for
/// shell syntaxes. For any other intent kind it still falls through to the pass-through path
/// (until Phase 9j.2 lands native K8s intent rendering).
/// </summary>
public class KubernetesApiIntentRendererTests
{
    private readonly Mock<IKubernetesApiContextScriptBuilder> _builderMock = new();
    private readonly KubernetesApiIntentRenderer _renderer;

    public KubernetesApiIntentRendererTests()
    {
        _renderer = new KubernetesApiIntentRenderer(_builderMock.Object);
    }

    // ========== Identity / capability checks ==========

    [Fact]
    public void CommunicationStyle_KubernetesApi()
    {
        _renderer.CommunicationStyle.ShouldBe(CommunicationStyle.KubernetesApi);
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
            async () => await _renderer.RenderAsync(null!, NewContext(legacy: new ScriptExecutionRequest()), CancellationToken.None));
    }

    [Fact]
    public async Task RenderAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await _renderer.RenderAsync(NewRunScriptIntent(), null!, CancellationToken.None));
    }

    // ========== RunScriptIntent: wrapping behaviour ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_BashSyntax_InvokesBuilderWithIntentBody()
    {
        SetupBuilder(returnValue: "wrapped-bash-body");
        var intent = NewRunScriptIntent(scriptBody: "echo from-intent", syntax: ScriptSyntax.Bash);

        await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        _builderMock.Verify(b => b.WrapWithContext(
            "echo from-intent",
            It.IsAny<ScriptContext>(),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_BashSyntax_ReturnsRequestWithWrappedBody()
    {
        SetupBuilder(returnValue: "wrapped-bash-body");
        var intent = NewRunScriptIntent(syntax: ScriptSyntax.Bash);

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldBe("wrapped-bash-body");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PowerShellSyntax_InvokesBuilder()
    {
        SetupBuilder(returnValue: "wrapped-pwsh-body");
        var intent = NewRunScriptIntent(scriptBody: "Write-Host hi", syntax: ScriptSyntax.PowerShell);

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldBe("wrapped-pwsh-body");
        _builderMock.Verify(b => b.WrapWithContext(
            "Write-Host hi",
            It.IsAny<ScriptContext>(),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PythonSyntax_DoesNotInvokeBuilder()
    {
        var intent = NewRunScriptIntent(scriptBody: "print('hi')", syntax: ScriptSyntax.Python);

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldBe("print('hi')");
        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.IsAny<ScriptContext>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PassesSyntaxToScriptContext()
    {
        SetupBuilder(returnValue: "wrapped");
        ScriptContext? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((_, ctx, _) => captured = ctx)
            .Returns("wrapped");

        await _renderer.RenderAsync(
            NewRunScriptIntent(syntax: ScriptSyntax.PowerShell),
            NewContext(legacy: null),
            CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PassesEndpointToScriptContext()
    {
        SetupBuilder(returnValue: "wrapped");
        ScriptContext? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((_, ctx, _) => captured = ctx)
            .Returns("wrapped");

        var endpoint = new EndpointContext { EndpointJson = "{ \"Host\": \"k8s.example.com\" }" };
        var target = new DeploymentTargetContext
        {
            Machine = new Machine { Id = 1, Name = "m1" },
            EndpointContext = endpoint,
            CommunicationStyle = CommunicationStyle.KubernetesApi
        };

        await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null, target: target), CancellationToken.None);

        captured!.Endpoint.ShouldBeSameAs(endpoint);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PassesEffectiveVariablesToScriptContext()
    {
        SetupBuilder(returnValue: "wrapped");
        ScriptContext? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((_, ctx, _) => captured = ctx)
            .Returns("wrapped");

        var vars = new List<VariableDto>
        {
            new() { Name = "Foo", Value = "Bar" }
        };

        await _renderer.RenderAsync(
            NewRunScriptIntent(),
            NewContext(legacy: null, variables: vars),
            CancellationToken.None);

        captured!.Variables.ShouldNotBeNull();
        captured.Variables.Select(v => v.Name).ShouldContain("Foo");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_CustomKubectlExecutableFromVariables_PassedToBuilder()
    {
        SetupBuilder(returnValue: "wrapped");
        string? capturedKubectl = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((_, _, kubectl) => capturedKubectl = kubectl)
            .Returns("wrapped");

        var vars = new List<VariableDto>
        {
            new() { Name = SpecialVariables.Kubernetes.CustomKubectlExecutable, Value = "/opt/bin/kubectl" }
        };

        await _renderer.RenderAsync(
            NewRunScriptIntent(),
            NewContext(legacy: null, variables: vars),
            CancellationToken.None);

        capturedKubectl.ShouldBe("/opt/bin/kubectl");
    }

    // ========== RunScriptIntent: intent-sourced + context-sourced fields ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_StepAndActionNameFromIntent()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewRunScriptIntent() with { StepName = "Deploy Step", ActionName = "Deploy Action" };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.StepName.ShouldBe("Deploy Step");
        rendered.ActionName.ShouldBe("Deploy Action");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_VariablesFromContext()
    {
        SetupBuilder(returnValue: "wrapped");
        var vars = new List<VariableDto>
        {
            new() { Name = "Foo", Value = "Bar" },
            new() { Name = "Secret", Value = "shh", IsSensitive = true }
        };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null, variables: vars), CancellationToken.None);

        rendered.Variables.ShouldNotBeNull();
        rendered.Variables.Select(v => v.Name).ShouldBe(new[] { "Foo", "Secret" });
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_MachineAndEndpointFromContext()
    {
        SetupBuilder(returnValue: "wrapped");
        var machine = new Machine { Id = 7, Name = "k8s-box" };
        var endpoint = new EndpointContext { EndpointJson = "{ \"ClusterUrl\": \"https://k8s\" }" };
        var target = new DeploymentTargetContext
        {
            Machine = machine,
            EndpointContext = endpoint,
            CommunicationStyle = CommunicationStyle.KubernetesApi
        };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null, target: target), CancellationToken.None);

        rendered.Machine.ShouldBeSameAs(machine);
        rendered.EndpointContext.ShouldBeSameAs(endpoint);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ServerTaskIdAndReleaseVersionFromContext()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(
            NewRunScriptIntent(),
            NewContext(legacy: null, serverTaskId: 99, releaseVersion: "2.5.0"),
            CancellationToken.None);

        rendered.ServerTaskId.ShouldBe(99);
        rendered.ReleaseVersion.ShouldBe("2.5.0");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_TimeoutPrefersIntentOverStepTimeout()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewRunScriptIntent() with { Timeout = TimeSpan.FromMinutes(3) };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null, stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_TimeoutFallsBackToStepTimeout()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null, stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ExecutionModeDirectScript()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        rendered.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Apply);
        rendered.PayloadKind.ShouldBe(PayloadKind.None);
    }

    // ========== RunScriptIntent: works without LegacyRequest ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_NullLegacyRequest_DoesNotThrow()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.ShouldNotBeNull();
        rendered.Files.ShouldNotBeNull();
        rendered.PackageReferences.ShouldNotBeNull();
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_NullLegacyRequest_PackageReferencesEmpty()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.PackageReferences.ShouldBeEmpty();
    }

    // ========== RunScriptIntent: legacy-forwarded fields (Phase 9j bridge) ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ForwardsLegacyPackageReferencesWhenPresent()
    {
        SetupBuilder(returnValue: "wrapped");
        var packages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/acme.zip", PackageId: "Acme.Web", Version: "1.0.0", SizeBytes: 123, Hash: "abc")
        };
        var legacy = new ScriptExecutionRequest { PackageReferences = packages };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy), CancellationToken.None);

        rendered.PackageReferences.ShouldBe(packages);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ForwardsLegacyFilesWhenPresent()
    {
        SetupBuilder(returnValue: "wrapped");
        var files = new Dictionary<string, byte[]> { { "extra.txt", new byte[] { 1, 2, 3 } } };
        var legacy = new ScriptExecutionRequest { Files = files };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy), CancellationToken.None);

        rendered.Files.ShouldBe(files);
    }

    // ========== Non-RunScript intents still pass through ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_PassesLegacyRequestThrough()
    {
        var legacy = new ScriptExecutionRequest { ScriptBody = "kubectl apply -f ." };
        var intent = new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            YamlFiles = new List<Squid.Core.Services.DeploymentExecution.Script.Files.DeploymentFile>(),
            Namespace = "default",
            ServerSideApply = false
        };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy), CancellationToken.None);

        rendered.ShouldBeSameAs(legacy);
    }

    [Fact]
    public async Task RenderAsync_NonRunScriptIntent_NullLegacy_ThrowsIntentRenderingException()
    {
        var intent = new ManualInterventionIntent { Name = "manual-intervention" };

        var ex = await Should.ThrowAsync<IntentRenderingException>(
            async () => await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None));

        ex.CommunicationStyle.ShouldBe(CommunicationStyle.KubernetesApi);
        ex.IntentName.ShouldBe("manual-intervention");
    }

    // ========== Helpers ==========

    private void SetupBuilder(string returnValue)
    {
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Returns(returnValue);
    }

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
                CommunicationStyle = CommunicationStyle.KubernetesApi,
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
