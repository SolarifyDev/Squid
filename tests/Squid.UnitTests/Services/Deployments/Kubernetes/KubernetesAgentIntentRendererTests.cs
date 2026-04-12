using System.Linq;
using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Rendering.Exceptions;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Phase 9j.3 — <see cref="KubernetesAgentIntentRenderer"/> natively renders
/// <see cref="RunScriptIntent"/> and <see cref="KubernetesApplyIntent"/> by constructing a
/// fresh <see cref="ScriptExecutionRequest"/> from the intent plus the
/// <see cref="IntentRenderContext"/>. For shell syntaxes (<see cref="ScriptSyntax.Bash"/>,
/// <see cref="ScriptSyntax.PowerShell"/>) the body is prepended with a
/// <c>kubectl config set-context --current --namespace=...</c> preamble (plus a namespace-
/// create probe for non-default namespaces). Non-shell syntaxes (Python, ...) are left
/// unwrapped. Namespace is resolved from <see cref="KubernetesApplyIntent.Namespace"/>
/// for apply intents; for <see cref="RunScriptIntent"/> — which has no namespace field —
/// the renderer reads <c>SpecialVariables.Kubernetes.Namespace</c> from
/// <see cref="IntentRenderContext.EffectiveVariables"/>.
/// </summary>
public class KubernetesAgentIntentRendererTests
{
    private readonly KubernetesAgentIntentRenderer _renderer = new();

    // ========== Identity / capability checks ==========

    [Fact]
    public void CommunicationStyle_KubernetesAgent()
    {
        _renderer.CommunicationStyle.ShouldBe(CommunicationStyle.KubernetesAgent);
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
    public async Task RenderAsync_RunScriptIntent_BashSyntax_PrependsNamespaceContext()
    {
        var intent = NewRunScriptIntent(scriptBody: "echo from-intent", syntax: ScriptSyntax.Bash);
        var vars = NamespaceVariable("production");

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null, variables: vars), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("kubectl config set-context --current --namespace=\"production\"");
        rendered.ScriptBody.ShouldContain("echo from-intent");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_BashSyntax_NamespacePreambleComesBeforeBody()
    {
        var intent = NewRunScriptIntent(scriptBody: "echo hello", syntax: ScriptSyntax.Bash);
        var vars = NamespaceVariable("staging");

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null, variables: vars), CancellationToken.None);

        var preambleIdx = rendered.ScriptBody.IndexOf("set-context", StringComparison.Ordinal);
        var bodyIdx = rendered.ScriptBody.IndexOf("echo hello", StringComparison.Ordinal);
        preambleIdx.ShouldBeGreaterThanOrEqualTo(0);
        preambleIdx.ShouldBeLessThan(bodyIdx);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PowerShellSyntax_PrependsNamespaceContext()
    {
        var intent = NewRunScriptIntent(scriptBody: "Write-Host hi", syntax: ScriptSyntax.PowerShell);
        var vars = NamespaceVariable("production");

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null, variables: vars), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("kubectl config set-context --current --namespace=\"production\"");
        rendered.ScriptBody.ShouldContain("| Out-Null");
        rendered.ScriptBody.ShouldContain("Write-Host hi");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PythonSyntax_ReturnsBodyUnwrapped()
    {
        var intent = NewRunScriptIntent(scriptBody: "print('hi')", syntax: ScriptSyntax.Python);

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldBe("print('hi')");
        rendered.ScriptBody.ShouldNotContain("kubectl config set-context");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_NoNamespaceVariable_FallsBackToDefaultNamespace()
    {
        var intent = NewRunScriptIntent(syntax: ScriptSyntax.Bash);

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("--namespace=\"default\"");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_EmptyNamespaceVariable_FallsBackToDefaultNamespace()
    {
        var intent = NewRunScriptIntent(syntax: ScriptSyntax.Bash);
        var vars = NamespaceVariable("");

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null, variables: vars), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("--namespace=\"default\"");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_CustomNamespace_UsesCreateNamespaceProbe()
    {
        var intent = NewRunScriptIntent(scriptBody: "true", syntax: ScriptSyntax.Bash);
        var vars = NamespaceVariable("my-ns");

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null, variables: vars), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("kubectl create namespace \"my-ns\"");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_DefaultNamespace_DoesNotEmitCreateProbe()
    {
        var intent = NewRunScriptIntent(scriptBody: "true", syntax: ScriptSyntax.Bash);
        var vars = NamespaceVariable("default");

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null, variables: vars), CancellationToken.None);

        rendered.ScriptBody.ShouldNotContain("kubectl create namespace");
    }

    // ========== RunScriptIntent: intent-sourced + context-sourced fields ==========

    [Fact]
    public async Task RenderAsync_RunScriptIntent_StepAndActionNameFromIntent()
    {
        var intent = NewRunScriptIntent() with { StepName = "Deploy Step", ActionName = "Deploy Action" };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.StepName.ShouldBe("Deploy Step");
        rendered.ActionName.ShouldBe("Deploy Action");
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_SyntaxFromIntent()
    {
        var intent = NewRunScriptIntent(syntax: ScriptSyntax.PowerShell);

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_VariablesFromContext()
    {
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
        var machine = new Machine { Id = 7, Name = "agent-01" };
        var endpoint = new EndpointContext { EndpointJson = "{}" };
        var target = new DeploymentTargetContext
        {
            Machine = machine,
            EndpointContext = endpoint,
            CommunicationStyle = CommunicationStyle.KubernetesAgent
        };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null, target: target), CancellationToken.None);

        rendered.Machine.ShouldBeSameAs(machine);
        rendered.EndpointContext.ShouldBeSameAs(endpoint);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ServerTaskIdAndReleaseVersionFromContext()
    {
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
        var intent = NewRunScriptIntent() with { Timeout = TimeSpan.FromMinutes(3) };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null, stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_TimeoutFallsBackToStepTimeout()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null, stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_ExecutionModeDirectScript()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        rendered.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Apply);
        rendered.PayloadKind.ShouldBe(PayloadKind.None);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_FilesAndPackagesEmpty()
    {
        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.ShouldNotBeNull();
        rendered.Files.ShouldNotBeNull();
        rendered.Files.ShouldBeEmpty();
        rendered.PackageReferences.ShouldNotBeNull();
        rendered.PackageReferences.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_PackageReferencesFromContext()
    {
        var packages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/a.zip", PackageId: "A", Version: "1.0.0", SizeBytes: 123, Hash: "abc")
        };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy: null, packageReferences: packages), CancellationToken.None);

        rendered.PackageReferences.ShouldBe(packages);
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_FilesAlwaysEmpty()
    {
        var legacyFiles = new Dictionary<string, byte[]> { ["extra.txt"] = new byte[] { 1, 2, 3 } };
        var legacy = new ScriptExecutionRequest { Files = legacyFiles };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy), CancellationToken.None);

        rendered.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenderAsync_RunScriptIntent_IgnoresLegacyPackageReferences()
    {
        var legacyPackages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/old.zip", PackageId: "Old", Version: "1.0.0", SizeBytes: 100, Hash: "old")
        };
        var contextPackages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/new.zip", PackageId: "New", Version: "2.0.0", SizeBytes: 200, Hash: "new")
        };
        var legacy = new ScriptExecutionRequest { PackageReferences = legacyPackages };

        var rendered = await _renderer.RenderAsync(NewRunScriptIntent(), NewContext(legacy, packageReferences: contextPackages), CancellationToken.None);

        rendered.PackageReferences.ShouldBe(contextPackages);
    }

    // ========== KubernetesApplyIntent: basic rendering ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_EmitsKubectlApplyPerFile()
    {
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("a.yaml", new byte[] { 0x41 }),
            DeploymentFile.Asset("b.yaml", new byte[] { 0x42 })
        });

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("kubectl apply -f \"./a.yaml\"");
        rendered.ScriptBody.ShouldContain("kubectl apply -f \"./b.yaml\"");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_FilesSortedDeterministically()
    {
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("zeta.yaml", new byte[] { 0x5A }),
            DeploymentFile.Asset("alpha.yaml", new byte[] { 0x41 })
        });

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        var alphaIdx = rendered.ScriptBody.IndexOf("alpha.yaml", StringComparison.Ordinal);
        var zetaIdx = rendered.ScriptBody.IndexOf("zeta.yaml", StringComparison.Ordinal);
        alphaIdx.ShouldBeGreaterThan(-1);
        zetaIdx.ShouldBeGreaterThan(-1);
        alphaIdx.ShouldBeLessThan(zetaIdx);
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_EmptyYamlFiles_EmitsEmptyApplyScript()
    {
        var intent = NewKubernetesApplyIntent(files: Array.Empty<DeploymentFile>());

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldNotContain("kubectl apply -f");
        rendered.Files.ShouldBeEmpty();
    }

    // ========== KubernetesApplyIntent: server-side-apply flag combinations ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ServerSideApplyDisabled_NoServerSideFlag()
    {
        var intent = NewKubernetesApplyIntent() with { ServerSideApply = false };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldNotContain("--server-side");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ServerSideApplyEnabled_UsesFieldManager()
    {
        var intent = NewKubernetesApplyIntent() with
        {
            ServerSideApply = true,
            FieldManager = "my-manager",
            ForceConflicts = false
        };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("--server-side");
        rendered.ScriptBody.ShouldContain("--field-manager=\"my-manager\"");
        rendered.ScriptBody.ShouldNotContain("--force-conflicts");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ForceConflictsEnabled_EmitsForceFlag()
    {
        var intent = NewKubernetesApplyIntent() with
        {
            ServerSideApply = true,
            FieldManager = "squid-deploy",
            ForceConflicts = true
        };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("--force-conflicts");
    }

    // ========== KubernetesApplyIntent: ObjectStatusCheck wait script ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ObjectStatusCheckDisabled_NoWaitScript()
    {
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("deployment.yaml", BytesFor("kind: Deployment\nmetadata:\n  name: api\n"))
        }) with { ObjectStatusCheck = false };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldNotContain("kubectl rollout status");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ObjectStatusCheckEnabled_AppendsRolloutWait()
    {
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("deployment.yaml", BytesFor("kind: Deployment\nmetadata:\n  name: api\n"))
        }) with
        {
            Namespace = "prod",
            ObjectStatusCheck = true,
            StatusCheckTimeoutSeconds = 120
        };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("kubectl rollout status \"deployment/api\" -n \"prod\" --timeout=120s");
    }

    // ========== KubernetesApplyIntent: syntax-specific path separator ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_BashSyntax_UsesForwardSlashInTargetPath()
    {
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("content/deploy.yaml", new byte[] { 0x41 })
        }) with { Syntax = ScriptSyntax.Bash };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("./content/deploy.yaml");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_PowerShellSyntax_UsesBackslashInTargetPath()
    {
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("content/deploy.yaml", new byte[] { 0x41 })
        }) with { Syntax = ScriptSyntax.PowerShell };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldContain(".\\content\\deploy.yaml");
    }

    // ========== KubernetesApplyIntent: namespace wrapping from intent ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_BashSyntax_WrapsWithNamespaceFromIntent()
    {
        var intent = NewKubernetesApplyIntent() with { Namespace = "production", Syntax = ScriptSyntax.Bash };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("kubectl config set-context --current --namespace=\"production\"");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_PowerShellSyntax_WrapsWithNamespace()
    {
        var intent = NewKubernetesApplyIntent() with { Namespace = "production", Syntax = ScriptSyntax.PowerShell };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("kubectl config set-context --current --namespace=\"production\"");
        rendered.ScriptBody.ShouldContain("| Out-Null");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_PythonSyntax_DoesNotWrapNamespace()
    {
        var intent = NewKubernetesApplyIntent() with { Namespace = "production", Syntax = ScriptSyntax.Python };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldNotContain("kubectl config set-context");
        rendered.ScriptBody.ShouldContain("kubectl apply -f");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_CustomNamespace_EmitsCreateProbe()
    {
        var intent = NewKubernetesApplyIntent() with { Namespace = "my-ns", Syntax = ScriptSyntax.Bash };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("kubectl create namespace \"my-ns\"");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_EmptyNamespace_UsesDefault()
    {
        var intent = NewKubernetesApplyIntent() with { Namespace = string.Empty, Syntax = ScriptSyntax.Bash };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.ScriptBody.ShouldContain("--namespace=\"default\"");
        rendered.ScriptBody.ShouldNotContain("kubectl create namespace");
    }

    // ========== KubernetesApplyIntent: intent-sourced + context-sourced fields ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_StepAndActionNameFromIntent()
    {
        var intent = NewKubernetesApplyIntent() with { StepName = "Apply Step", ActionName = "Apply Action" };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.StepName.ShouldBe("Apply Step");
        rendered.ActionName.ShouldBe("Apply Action");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_SyntaxFromIntent()
    {
        var intent = NewKubernetesApplyIntent() with { Syntax = ScriptSyntax.PowerShell };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.Syntax.ShouldBe(ScriptSyntax.PowerShell);
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_VariablesFromContext()
    {
        var vars = new List<VariableDto> { new() { Name = "Foo", Value = "Bar" } };

        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(legacy: null, variables: vars), CancellationToken.None);

        rendered.Variables.ShouldNotBeNull();
        rendered.Variables.Select(v => v.Name).ShouldContain("Foo");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_MachineAndEndpointFromContext()
    {
        var machine = new Machine { Id = 7, Name = "agent-01" };
        var endpoint = new EndpointContext { EndpointJson = "{}" };
        var target = new DeploymentTargetContext
        {
            Machine = machine,
            EndpointContext = endpoint,
            CommunicationStyle = CommunicationStyle.KubernetesAgent
        };

        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(legacy: null, target: target), CancellationToken.None);

        rendered.Machine.ShouldBeSameAs(machine);
        rendered.EndpointContext.ShouldBeSameAs(endpoint);
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ServerTaskIdAndReleaseVersionFromContext()
    {
        var rendered = await _renderer.RenderAsync(
            NewKubernetesApplyIntent(),
            NewContext(legacy: null, serverTaskId: 99, releaseVersion: "2.5.0"),
            CancellationToken.None);

        rendered.ServerTaskId.ShouldBe(99);
        rendered.ReleaseVersion.ShouldBe("2.5.0");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_TimeoutPrefersIntentOverStepTimeout()
    {
        var intent = NewKubernetesApplyIntent() with { Timeout = TimeSpan.FromMinutes(3) };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null, stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_TimeoutFallsBackToStepTimeout()
    {
        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(legacy: null, stepTimeout: TimeSpan.FromMinutes(7)), CancellationToken.None);

        rendered.Timeout.ShouldBe(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_ExecutionModeDirectScript()
    {
        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
        rendered.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Apply);
        rendered.PayloadKind.ShouldBe(PayloadKind.None);
    }

    // ========== KubernetesApplyIntent: Files derived from intent (not LegacyRequest) ==========

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_FilesComeFromIntent()
    {
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("configmap.yaml", new byte[] { 0x43, 0x4D }),
            DeploymentFile.Asset("secret.yaml", new byte[] { 0x53, 0x45 })
        });

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None);

        rendered.Files.ShouldNotBeNull();
        rendered.Files.Count.ShouldBe(2);
        rendered.Files["configmap.yaml"].ShouldBe(new byte[] { 0x43, 0x4D });
        rendered.Files["secret.yaml"].ShouldBe(new byte[] { 0x53, 0x45 });
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_IgnoresLegacyFiles()
    {
        var legacyFiles = new Dictionary<string, byte[]> { ["legacy.yaml"] = new byte[] { 0xFF } };
        var legacy = new ScriptExecutionRequest { Files = legacyFiles };
        var intent = NewKubernetesApplyIntent(files: new[]
        {
            DeploymentFile.Asset("intent.yaml", new byte[] { 0x01 })
        });

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy), CancellationToken.None);

        rendered.Files.ShouldContainKey("intent.yaml");
        rendered.Files.ShouldNotContainKey("legacy.yaml");
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_NullLegacy_PackageReferencesEmpty()
    {
        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(legacy: null), CancellationToken.None);

        rendered.PackageReferences.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_PackageReferencesFromContext()
    {
        var packages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/chart.tgz", PackageId: "chart", Version: "1.0.0", SizeBytes: 123, Hash: "abc")
        };

        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(legacy: null, packageReferences: packages), CancellationToken.None);

        rendered.PackageReferences.ShouldBe(packages);
    }

    [Fact]
    public async Task RenderAsync_KubernetesApplyIntent_IgnoresLegacyPackageReferences()
    {
        var legacyPackages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/old.tgz", PackageId: "old-chart", Version: "1.0.0", SizeBytes: 100, Hash: "old")
        };
        var contextPackages = new List<PackageAcquisitionResult>
        {
            new(LocalPath: "/tmp/new.tgz", PackageId: "new-chart", Version: "2.0.0", SizeBytes: 200, Hash: "new")
        };
        var legacy = new ScriptExecutionRequest { PackageReferences = legacyPackages };

        var rendered = await _renderer.RenderAsync(NewKubernetesApplyIntent(), NewContext(legacy, packageReferences: contextPackages), CancellationToken.None);

        rendered.PackageReferences.ShouldBe(contextPackages);
    }

    // ========== Non-native intents still pass through ==========

    [Fact]
    public async Task RenderAsync_NonNativeIntent_NullLegacy_ThrowsIntentRenderingException()
    {
        var intent = new ManualInterventionIntent { Name = "manual-intervention" };

        var ex = await Should.ThrowAsync<IntentRenderingException>(
            async () => await _renderer.RenderAsync(intent, NewContext(legacy: null), CancellationToken.None));

        ex.CommunicationStyle.ShouldBe(CommunicationStyle.KubernetesAgent);
        ex.IntentName.ShouldBe("manual-intervention");
    }

    [Fact]
    public async Task RenderAsync_NonNativeIntent_WithLegacy_ReturnsLegacyUnchanged()
    {
        var legacy = new ScriptExecutionRequest { ScriptBody = "echo from legacy" };
        var intent = new ManualInterventionIntent { Name = "manual-intervention" };

        var rendered = await _renderer.RenderAsync(intent, NewContext(legacy), CancellationToken.None);

        rendered.ShouldBeSameAs(legacy);
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

    private static List<VariableDto> NamespaceVariable(string namespaceValue)
    {
        return new List<VariableDto>
        {
            new() { Name = "Squid.Action.Kubernetes.Namespace", Value = namespaceValue }
        };
    }

    private static byte[] BytesFor(string content) => Encoding.UTF8.GetBytes(content);

    private static IntentRenderContext NewContext(
        ScriptExecutionRequest? legacy,
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
                CommunicationStyle = CommunicationStyle.KubernetesAgent,
                EndpointContext = new EndpointContext()
            },
            Step = new DeploymentStepDto { Name = "step-1" },
            EffectiveVariables = variables ?? new List<VariableDto>(),
            ServerTaskId = serverTaskId,
            ReleaseVersion = releaseVersion,
            StepTimeout = stepTimeout,
            PackageReferences = packageReferences ?? new List<PackageAcquisitionResult>(),
            LegacyRequest = legacy
        };
    }
}
