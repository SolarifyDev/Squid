using System.Linq;
using System.Text;
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
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Phase 9j.1 / 9j.2 — <see cref="KubernetesApiIntentRenderer"/> natively renders
/// <see cref="RunScriptIntent"/> and <see cref="KubernetesApplyIntent"/> by constructing a
/// fresh <see cref="ScriptExecutionRequest"/> from the intent plus the
/// <see cref="IntentRenderContext"/>. For shell syntaxes the rendered body is wrapped with
/// the cluster's kubectl context via <see cref="IKubernetesApiContextScriptBuilder"/>; for
/// <see cref="KubernetesApplyIntent"/> the body is the per-file <c>kubectl apply -f</c>
/// pipeline plus a <see cref="KubernetesResourceWaitBuilder"/> block when
/// <see cref="KubernetesApplyIntent.ObjectStatusCheck"/> is set. Intents without a native
/// renderer still fall through to the legacy pass-through path.
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
    public void CanRender_KubernetesApplyIntent_True()
    {
        _renderer.CanRender(NewKubernetesApplyIntent()).ShouldBeTrue();
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

    // ========== TargetNamespace propagation ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_TargetNamespace_PropagatedToRequest()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewRunScriptIntent(syntax: ScriptSyntax.Bash);

        var rendered = await _renderer.RenderAsync(intent, NewContext(targetNamespace: "production"), CancellationToken.None);

        rendered.TargetNamespace.ShouldBe("production");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_TargetNamespace_PropagatedToRequest()
    {
        var intent = NewKubernetesApplyIntent();

        var rendered = await _renderer.RenderAsync(intent, NewContext(targetNamespace: "staging"), CancellationToken.None);

        rendered.TargetNamespace.ShouldBe("staging");
    }

    [Fact]
    public async Task RenderAsync_HelmUpgradeIntent_TargetNamespace_PropagatedToRequest()
    {
        var intent = NewHelmUpgradeIntent();

        var rendered = await _renderer.RenderAsync(intent, NewContext(targetNamespace: "helm-ns"), CancellationToken.None);

        rendered.TargetNamespace.ShouldBe("helm-ns");
    }

    [Fact]
    public async Task RenderAsync_KustomizeIntent_TargetNamespace_PropagatedToRequest()
    {
        var intent = NewKustomizeIntent();

        var rendered = await _renderer.RenderAsync(intent, NewContext(targetNamespace: "kustomize-ns"), CancellationToken.None);

        rendered.TargetNamespace.ShouldBe("kustomize-ns");
    }

    // ========== RunScriptIntent: wrapping behaviour ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_BashSyntax_InvokesBuilderWithIntentBody()
    {
        SetupBuilder(returnValue: "wrapped-bash-body");
        var intent = NewRunScriptIntent(scriptBody: "echo from-intent", syntax: ScriptSyntax.Bash);

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

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

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.ScriptBody.ShouldBe("wrapped-bash-body");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PowerShellSyntax_InvokesBuilder()
    {
        SetupBuilder(returnValue: "wrapped-pwsh-body");
        var intent = NewRunScriptIntent(scriptBody: "Write-Host hi", syntax: ScriptSyntax.PowerShell);

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

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

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

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
            NewContext(),
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

        await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(target: target), CancellationToken.None);

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
            NewContext(variables: vars),
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
            NewContext(variables: vars),
            CancellationToken.None);

        capturedKubectl.ShouldBe("/opt/bin/kubectl");
    }

    // ========== RunScriptIntent: intent-sourced + context-sourced fields ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_StepAndActionNameFromIntent()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewRunScriptIntent() with { StepName = "Deploy Step", ActionName = "Deploy Action" };

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

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

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(variables: vars), CancellationToken.None);

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

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(target: target), CancellationToken.None);

        rendered.Machine.ShouldBeSameAs(machine);
        rendered.EndpointContext.ShouldBeSameAs(endpoint);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ServerTaskIdAndReleaseVersionFromContext()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(
            NewRunScriptIntent(),
            NewContext(serverTaskId: 99, releaseVersion: "2.5.0"),
            CancellationToken.None);

        rendered.ServerTaskId.ShouldBe(99);
        rendered.ReleaseVersion.ShouldBe("2.5.0");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_TimeoutPrefersIntentOverStepTimeout()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewRunScriptIntent() with { Timeout = TimeSpan.FromMinutes(3) };

        var rendered = await _renderer.RenderAsync(intent, NewContext(stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_TimeoutFallsBackToStepTimeout()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ExecutionModeDirectScript()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(), CancellationToken.None);

        rendered.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        rendered.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Apply);
        rendered.PayloadKind.ShouldBe(PayloadKind.None);
    }

    // ========== RunScriptIntent: native rendering ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_NativeRendering_DoesNotThrow()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(), CancellationToken.None);

        rendered.ShouldNotBeNull();
        rendered.DeploymentFiles.ShouldNotBeNull();
        rendered.PackageReferences.ShouldNotBeNull();
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PackageReferencesEmptyByDefault()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(), CancellationToken.None);

        rendered.PackageReferences.ShouldBeEmpty();
    }

    // ========== RunScriptIntent: context-sourced PackageReferences ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PackageReferencesFromContext()
    {
        SetupBuilder(returnValue: "wrapped");
        var packages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/acme.zip", PackageId: "Acme.Web", Version: "1.0.0", SizeBytes: 123, Hash: "abc")
        };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(packageReferences: packages), CancellationToken.None);

        rendered.PackageReferences.ShouldBe(packages);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PackageReferencesFromContextOnly()
    {
        SetupBuilder(returnValue: "wrapped");
        var contextPackages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/new.zip", PackageId: "New", Version: "2.0.0", SizeBytes: 200, Hash: "new")
        };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(packageReferences: contextPackages), CancellationToken.None);

        rendered.PackageReferences.ShouldBe(contextPackages);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_FilesAlwaysEmpty()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(), CancellationToken.None);

        rendered.DeploymentFiles.Count.ShouldBe(0);
    }

    // ========== KubernetesApplyIntent: basic rendering ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_EmitsKubectlApplyPerFile()
    {
        SetupBuilder(returnValue: "wrapped");
        string? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((body, _, _) => captured = body)
            .Returns("wrapped");

        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("a.yaml", new byte[] { 0x41 }),
            DeploymentFile.Asset("b.yaml", new byte[] { 0x42 })
        });

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.ShouldContain("kubectl apply -f \"./a.yaml\"");
        captured.ShouldContain("kubectl apply -f \"./b.yaml\"");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_FilesSortedDeterministically()
    {
        SetupBuilder(returnValue: "wrapped");
        string? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((body, _, _) => captured = body)
            .Returns("wrapped");

        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("zeta.yaml", new byte[] { 0x5A }),
            DeploymentFile.Asset("alpha.yaml", new byte[] { 0x41 })
        });

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        captured.ShouldNotBeNull();
        var alphaIdx = captured!.IndexOf("alpha.yaml", StringComparison.Ordinal);
        var zetaIdx = captured.IndexOf("zeta.yaml", StringComparison.Ordinal);
        alphaIdx.ShouldBeGreaterThan(-1);
        zetaIdx.ShouldBeGreaterThan(-1);
        alphaIdx.ShouldBeLessThan(zetaIdx);
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_EmptyYamlFiles_EmitsEmptyApplyScript()
    {
        SetupBuilder(returnValue: "wrapped");
        string? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((body, _, _) => captured = body)
            .Returns("wrapped");

        var intent = NewKubernetesApplyIntent(files: Array.Empty<DeploymentFile>());

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        captured.ShouldBe(string.Empty);
        rendered.DeploymentFiles.Count.ShouldBe(0);
    }

    // ========== KubernetesApplyIntent: server-side-apply flag combinations ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ServerSideApplyDisabled_NoServerSideFlag()
    {
        SetupBuilder(returnValue: "wrapped");
        string? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((body, _, _) => captured = body)
            .Returns("wrapped");

        var intent = NewKubernetesApplyIntent() with { ServerSideApply = false };

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.ShouldNotContain("--server-side");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ServerSideApplyEnabled_UsesFieldManager()
    {
        SetupBuilder(returnValue: "wrapped");
        string? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((body, _, _) => captured = body)
            .Returns("wrapped");

        var intent = NewKubernetesApplyIntent() with
        {
            ServerSideApply = true,
            FieldManager = "my-manager",
            ForceConflicts = false
        };

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.ShouldContain("--server-side");
        captured.ShouldContain("--field-manager=\"my-manager\"");
        captured.ShouldNotContain("--force-conflicts");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ForceConflictsEnabled_EmitsForceFlag()
    {
        SetupBuilder(returnValue: "wrapped");
        string? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((body, _, _) => captured = body)
            .Returns("wrapped");

        var intent = NewKubernetesApplyIntent() with
        {
            ServerSideApply = true,
            FieldManager = "squid-deploy",
            ForceConflicts = true
        };

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.ShouldContain("--force-conflicts");
    }

    // ========== KubernetesApplyIntent: ObjectStatusCheck wait script ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ObjectStatusCheckDisabled_NoWaitScript()
    {
        SetupBuilder(returnValue: "wrapped");
        string? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((body, _, _) => captured = body)
            .Returns("wrapped");

        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("deployment.yaml", BytesFor("kind: Deployment\nmetadata:\n  name: api\n"))
        }) with { ObjectStatusCheck = false };

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.ShouldNotContain("kubectl rollout status");
        captured.ShouldNotContain("Resource status check");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ObjectStatusCheckEnabled_AppendsRolloutWait()
    {
        SetupBuilder(returnValue: "wrapped");
        string? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((body, _, _) => captured = body)
            .Returns("wrapped");

        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("deployment.yaml", BytesFor("kind: Deployment\nmetadata:\n  name: api\n"))
        }) with
        {
            Namespace = "prod",
            ObjectStatusCheck = true,
            StatusCheckTimeoutSeconds = 120
        };

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.ShouldContain("kubectl rollout status \"deployment/api\" -n \"prod\" --timeout=120s");
    }

    // ========== KubernetesApplyIntent: syntax-specific path separator ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_PowerShellSyntax_UsesBackslashInTargetPath()
    {
        SetupBuilder(returnValue: "wrapped");
        string? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((body, _, _) => captured = body)
            .Returns("wrapped");

        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("content/deploy.yaml", new byte[] { 0x41 })
        }) with { Syntax = ScriptSyntax.PowerShell };

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.ShouldContain(".\\content\\deploy.yaml");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_BashSyntax_UsesForwardSlashInTargetPath()
    {
        SetupBuilder(returnValue: "wrapped");
        string? captured = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((body, _, _) => captured = body)
            .Returns("wrapped");

        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("content/deploy.yaml", new byte[] { 0x41 })
        }) with { Syntax = ScriptSyntax.Bash };

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.ShouldContain("./content/deploy.yaml");
    }

    // ========== KubernetesApplyIntent: kubectl-context wrapping ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_BashSyntax_WrapsWithKubectlContext()
    {
        SetupBuilder(returnValue: "wrapped-apply");
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("deploy.yaml", new byte[] { 0x41 })
        });

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.ScriptBody.ShouldBe("wrapped-apply");
        _builderMock.Verify(b => b.WrapWithContext(
            It.Is<string>(s => s.Contains("kubectl apply -f")),
            It.IsAny<ScriptContext>(),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_PythonSyntax_DoesNotWrapWithKubectlContext()
    {
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("deploy.yaml", new byte[] { 0x41 })
        }) with { Syntax = ScriptSyntax.Python };

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("kubectl apply -f");
        _builderMock.Verify(b => b.WrapWithContext(
            It.IsAny<string>(),
            It.IsAny<ScriptContext>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_CustomKubectlExecutableFromVariables_PassedToBuilder()
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
            NewKubernetesApplyIntent(),
            NewContext(variables: vars),
            CancellationToken.None);

        capturedKubectl.ShouldBe("/opt/bin/kubectl");
    }

    // ========== KubernetesApplyIntent: intent-sourced + context-sourced fields ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_StepAndActionNameFromIntent()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewKubernetesApplyIntent() with { StepName = "Apply Step", ActionName = "Apply Action" };

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.StepName.ShouldBe("Apply Step");
        rendered.ActionName.ShouldBe("Apply Action");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_SyntaxFromIntent()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewKubernetesApplyIntent() with { Syntax = ScriptSyntax.PowerShell };

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_VariablesFromContext()
    {
        SetupBuilder(returnValue: "wrapped");
        var vars = new List<VariableDto>
        {
            new() { Name = "Foo", Value = "Bar" }
        };

        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(variables: vars), CancellationToken.None);

        rendered.Variables.ShouldNotBeNull();
        rendered.Variables.Select(v => v.Name).ShouldContain("Foo");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_MachineAndEndpointFromContext()
    {
        SetupBuilder(returnValue: "wrapped");
        var machine = new Machine { Id = 7, Name = "k8s-box" };
        var endpoint = new EndpointContext { EndpointJson = "{}" };
        var target = new DeploymentTargetContext
        {
            Machine = machine,
            EndpointContext = endpoint,
            CommunicationStyle = CommunicationStyle.KubernetesApi
        };

        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(target: target), CancellationToken.None);

        rendered.Machine.ShouldBeSameAs(machine);
        rendered.EndpointContext.ShouldBeSameAs(endpoint);
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ServerTaskIdAndReleaseVersionFromContext()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(
            NewKubernetesApplyIntent(),
            NewContext(serverTaskId: 99, releaseVersion: "2.5.0"),
            CancellationToken.None);

        rendered.ServerTaskId.ShouldBe(99);
        rendered.ReleaseVersion.ShouldBe("2.5.0");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_TimeoutPrefersIntentOverStepTimeout()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewKubernetesApplyIntent() with { Timeout = TimeSpan.FromMinutes(3) };

        var rendered = await _renderer.RenderAsync(intent, NewContext(stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_TimeoutFallsBackToStepTimeout()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ExecutionModeDirectScript()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(), CancellationToken.None);

        rendered.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        rendered.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Apply);
        rendered.PayloadKind.ShouldBe(PayloadKind.None);
    }

    // ========== KubernetesApplyIntent: Files derived from intent ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_FilesComeFromIntent()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("configmap.yaml", new byte[] { 0x43, 0x4D }),
            DeploymentFile.Asset("secret.yaml", new byte[] { 0x53, 0x45 })
        });

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.DeploymentFiles.ShouldNotBeNull();
        rendered.DeploymentFiles.Count.ShouldBe(2);
        rendered.DeploymentFiles.Single(f => f.RelativePath == "configmap.yaml").Content.ShouldBe(new byte[] { 0x43, 0x4D });
        rendered.DeploymentFiles.Single(f => f.RelativePath == "secret.yaml").Content.ShouldBe(new byte[] { 0x53, 0x45 });
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_FilesFromIntentOnly()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("intent.yaml", new byte[] { 0x01 })
        });

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.DeploymentFiles.Any(f => f.RelativePath == "intent.yaml").ShouldBeTrue();
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_PackageReferencesEmptyByDefault()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(), CancellationToken.None);

        rendered.PackageReferences.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_PackageReferencesFromContext()
    {
        SetupBuilder(returnValue: "wrapped");
        var packages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/chart.tgz", PackageId: "chart", Version: "1.0.0", SizeBytes: 123, Hash: "abc")
        };

        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(packageReferences: packages), CancellationToken.None);

        rendered.PackageReferences.ShouldBe(packages);
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_PackageReferencesFromContextOnly()
    {
        SetupBuilder(returnValue: "wrapped");
        var contextPackages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/new.tgz", PackageId: "new-chart", Version: "2.0.0", SizeBytes: 200, Hash: "new")
        };

        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(packageReferences: contextPackages), CancellationToken.None);

        rendered.PackageReferences.ShouldBe(contextPackages);
    }

    // ========== HelmUpgradeIntent: native rendering ==========

    [Fact]
    public async Task RenderAsync_HelmUpgradeIntent_BashSyntax_InvokesBuilder()
    {
        SetupBuilder(returnValue: "wrapped-helm");
        var intent = NewHelmUpgradeIntent(syntax: ScriptSyntax.Bash);

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.ScriptBody.ShouldBe("wrapped-helm");
        _builderMock.Verify(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RenderAsync_HelmUpgradeIntent_ScriptContainsHelmUpgrade()
    {
        string? capturedScript = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((script, _, _) => capturedScript = script)
            .Returns("wrapped");

        var intent = NewHelmUpgradeIntent(releaseName: "my-app", chartReference: "bitnami/nginx");

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        capturedScript.ShouldNotBeNull();
        capturedScript.ShouldContain("upgrade --install");
        capturedScript.ShouldContain("my-app");
        capturedScript.ShouldContain("bitnami/nginx");
    }

    [Fact]
    public async Task RenderAsync_HelmUpgradeIntent_SyntaxFromIntent()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewHelmUpgradeIntent(syntax: ScriptSyntax.PowerShell);

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public async Task RenderAsync_HelmUpgradeIntent_StepAndActionNameFromIntent()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewHelmUpgradeIntent() with { StepName = "Helm Step", ActionName = "Helm Action" };

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.StepName.ShouldBe("Helm Step");
        rendered.ActionName.ShouldBe("Helm Action");
    }

    [Fact]
    public async Task RenderAsync_HelmUpgradeIntent_ContextFieldsPopulated()
    {
        SetupBuilder(returnValue: "wrapped");
        var packages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/chart.tgz", PackageId: "chart", Version: "1.0.0", SizeBytes: 123, Hash: "abc")
        };

        var rendered = await _renderer.RenderAsync(NewHelmUpgradeIntent(), NewContext(serverTaskId: 99, releaseVersion: "2.0.0", packageReferences: packages), CancellationToken.None);

        rendered.ServerTaskId.ShouldBe(99);
        rendered.ReleaseVersion.ShouldBe("2.0.0");
        rendered.PackageReferences.ShouldBe(packages);
        rendered.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
    }

    [Fact]
    public async Task RenderAsync_HelmUpgradeIntent_ValuesFilesIncludedInFiles()
    {
        SetupBuilder(returnValue: "wrapped");
        var valuesFiles = new List<DeploymentFile>
        {
            DeploymentFile.Asset("values-0.yaml", BytesFor("key: value")),
            DeploymentFile.Asset("values-1.yaml", BytesFor("other: data"))
        };
        var intent = NewHelmUpgradeIntent() with { ValuesFiles = valuesFiles };

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.DeploymentFiles.Count.ShouldBe(2);
        rendered.DeploymentFiles.Any(f => f.RelativePath == "values-0.yaml").ShouldBeTrue();
        rendered.DeploymentFiles.Any(f => f.RelativePath == "values-1.yaml").ShouldBeTrue();
    }

    [Fact]
    public async Task RenderAsync_HelmUpgradeIntent_EmptyValuesFiles_EmptyFiles()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewHelmUpgradeIntent() with { ValuesFiles = Array.Empty<DeploymentFile>() };

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.DeploymentFiles.Count.ShouldBe(0);
    }

    [Fact]
    public async Task RenderAsync_HelmUpgradeIntent_TimeoutFallsBackToStepTimeout()
    {
        SetupBuilder(returnValue: "wrapped");
        var stepTimeout = TimeSpan.FromMinutes(10);

        var rendered = await _renderer.RenderAsync(NewHelmUpgradeIntent(), NewContext(stepTimeout: stepTimeout), CancellationToken.None);

        rendered.Timeout.ShouldBe(stepTimeout);
    }

    // ========== KubernetesKustomizeIntent: native rendering ==========

    [Fact]
    public async Task RenderAsync_KustomizeIntent_BashSyntax_InvokesBuilder()
    {
        SetupBuilder(returnValue: "wrapped-kustomize");
        var intent = NewKustomizeIntent(syntax: ScriptSyntax.Bash);

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.ScriptBody.ShouldBe("wrapped-kustomize");
        _builderMock.Verify(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RenderAsync_KustomizeIntent_ScriptContainsKustomizeBuildAndApply()
    {
        string? capturedScript = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((script, _, _) => capturedScript = script)
            .Returns("wrapped");

        var intent = NewKustomizeIntent(overlayPath: "overlays/production");

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        capturedScript.ShouldNotBeNull();
        capturedScript.ShouldContain("kustomize");
        capturedScript.ShouldContain("overlays/production");
        capturedScript.ShouldContain("kubectl apply");
    }

    [Fact]
    public async Task RenderAsync_KustomizeIntent_SyntaxFromIntent()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewKustomizeIntent(syntax: ScriptSyntax.PowerShell);

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public async Task RenderAsync_KustomizeIntent_StepAndActionNameFromIntent()
    {
        SetupBuilder(returnValue: "wrapped");
        var intent = NewKustomizeIntent() with { StepName = "Kustomize Step", ActionName = "Kustomize Action" };

        var rendered = await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        rendered.StepName.ShouldBe("Kustomize Step");
        rendered.ActionName.ShouldBe("Kustomize Action");
    }

    [Fact]
    public async Task RenderAsync_KustomizeIntent_ContextFieldsPopulated()
    {
        SetupBuilder(returnValue: "wrapped");
        var packages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/pkg.tgz", PackageId: "pkg", Version: "1.0.0", SizeBytes: 100, Hash: "h")
        };

        var rendered = await _renderer.RenderAsync(NewKustomizeIntent(), NewContext(serverTaskId: 77, releaseVersion: "3.0.0", packageReferences: packages), CancellationToken.None);

        rendered.ServerTaskId.ShouldBe(77);
        rendered.ReleaseVersion.ShouldBe("3.0.0");
        rendered.PackageReferences.ShouldBe(packages);
        rendered.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
    }

    [Fact]
    public async Task RenderAsync_KustomizeIntent_FilesAlwaysEmpty()
    {
        SetupBuilder(returnValue: "wrapped");

        var rendered = await _renderer.RenderAsync(NewKustomizeIntent(), NewContext(), CancellationToken.None);

        rendered.DeploymentFiles.Count.ShouldBe(0);
    }

    [Fact]
    public async Task RenderAsync_KustomizeIntent_ServerSideApply_IncludesFlags()
    {
        string? capturedScript = null;
        _builderMock
            .Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>()))
            .Callback<string, ScriptContext, string>((script, _, _) => capturedScript = script)
            .Returns("wrapped");

        var intent = NewKustomizeIntent() with { ServerSideApply = true, FieldManager = "my-mgr", ForceConflicts = true };

        await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None);

        capturedScript.ShouldNotBeNull();
        capturedScript.ShouldContain("--server-side");
        capturedScript.ShouldContain("my-mgr");
        capturedScript.ShouldContain("--force-conflicts");
    }

    // ========== Unsupported intents throw ==========

    [Fact]
    public async Task RenderAsync_UnsupportedIntent_ThrowsIntentRenderingException()
    {
        var intent = new ManualInterventionIntent { Name = "manual-intervention" };

        var ex = await Should.ThrowAsync<IntentRenderingException>(
            async () => await _renderer.RenderAsync(intent, NewContext(), CancellationToken.None));

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

    private static KubernetesApplyIntent NewKubernetesApplyIntent(IReadOnlyList<DeploymentFile>? files = null)
    {
        var yamlFiles = files ?? new[]
        {
            DeploymentFile.Asset("deploy.yaml", new byte[] { 0x41 })
        };

        return new KubernetesApplyIntent
        {
            Name = "k8s-apply",
            StepName = "step-1",
            ActionName = "action-1",
            YamlFiles = yamlFiles,
            Assets = yamlFiles,
            Namespace = "default",
            Syntax = ScriptSyntax.Bash,
            ServerSideApply = false,
            FieldManager = "squid-deploy",
            ForceConflicts = false,
            ObjectStatusCheck = false,
            StatusCheckTimeoutSeconds = 300
        };
    }

    private static HelmUpgradeIntent NewHelmUpgradeIntent(string releaseName = "my-release", string chartReference = "my-chart", ScriptSyntax syntax = ScriptSyntax.Bash)
    {
        return new HelmUpgradeIntent
        {
            Name = "helm-upgrade",
            StepName = "step-1",
            ActionName = "action-1",
            Syntax = syntax,
            ReleaseName = releaseName,
            ChartReference = chartReference
        };
    }

    private static KubernetesKustomizeIntent NewKustomizeIntent(string overlayPath = ".", ScriptSyntax syntax = ScriptSyntax.Bash)
    {
        return new KubernetesKustomizeIntent
        {
            Name = "k8s-kustomize-apply",
            StepName = "step-1",
            ActionName = "action-1",
            Syntax = syntax,
            OverlayPath = overlayPath
        };
    }

    private static byte[] BytesFor(string content) => Encoding.UTF8.GetBytes(content);

    private static IntentRenderContext NewContext(
        List<VariableDto>? variables = null,
        DeploymentTargetContext? target = null,
        int serverTaskId = 42,
        string? releaseVersion = "1.0.0",
        TimeSpan? stepTimeout = null,
        List<PackageAcquisitionResult>? packageReferences = null,
        string? targetNamespace = null)
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
            TargetNamespace = targetNamespace,
            PackageReferences = packageReferences ?? new List<PackageAcquisitionResult>()
        };
    }
}
