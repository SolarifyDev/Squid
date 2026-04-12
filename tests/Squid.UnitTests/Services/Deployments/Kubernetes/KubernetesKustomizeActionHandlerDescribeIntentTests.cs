using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Phase 9c.3 — verifies that <see cref="KubernetesKustomizeActionHandler"/> overrides
/// <c>DescribeIntentAsync</c> and emits a <see cref="KubernetesKustomizeIntent"/> directly,
/// with a stable semantic name (<c>k8s-kustomize-apply</c>) and overlay/path/args/namespace
/// fields populated from the action properties. Unlike <see cref="KubernetesApplyIntent"/>,
/// a kustomize intent carries NO pre-rendered YamlFiles — the manifests only exist after
/// <c>kustomize build</c> runs on the target. The legacy <c>PrepareAsync</c> path is
/// preserved until Phase 9g flips the pipeline.
/// </summary>
public class KubernetesKustomizeActionHandlerDescribeIntentTests
{
    private readonly KubernetesKustomizeActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(
        string actionName = "apply-kustomize",
        string overlayPath = null,
        string customKustomizePath = null,
        string additionalArgs = null,
        string namespaceValue = null,
        bool serverSideApply = false,
        string fieldManager = null,
        bool forceConflicts = false)
    {
        var properties = new List<DeploymentActionPropertyDto>();

        if (overlayPath != null)
            properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesKustomize.OverlayPath",
                PropertyValue = overlayPath
            });

        if (customKustomizePath != null)
            properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesKustomize.CustomKustomizePath",
                PropertyValue = customKustomizePath
            });

        if (additionalArgs != null)
            properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesKustomize.AdditionalArgs",
                PropertyValue = additionalArgs
            });

        if (namespaceValue != null)
            properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.Namespace",
                PropertyValue = namespaceValue
            });

        if (serverSideApply)
            properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.Kubernetes.ServerSideApply.Enabled",
                PropertyValue = "True"
            });

        if (fieldManager != null)
            properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.Kubernetes.ServerSideApply.FieldManager",
                PropertyValue = fieldManager
            });

        if (forceConflicts)
            properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.Kubernetes.ServerSideApply.ForceConflicts",
                PropertyValue = "True"
            });

        return new DeploymentActionDto
        {
            Name = actionName,
            ActionType = SpecialVariables.ActionTypes.KubernetesKustomize,
            Properties = properties
        };
    }

    private static ActionExecutionContext CreateContext(
        string stepName = "Apply Overlay",
        DeploymentActionDto action = null) => new()
    {
        Step = new DeploymentStepDto { Name = stepName },
        Action = action ?? CreateAction()
    };

    [Fact]
    public async Task DescribeIntentAsync_ReturnsKubernetesKustomizeIntent()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ShouldBeOfType<KubernetesKustomizeIntent>();
    }

    [Fact]
    public async Task DescribeIntentAsync_NameIsK8sKustomizeApply()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldBe("k8s-kustomize-apply");
    }

    [Fact]
    public async Task DescribeIntentAsync_DoesNotUseLegacyName()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldNotStartWith("legacy:");
    }

    [Fact]
    public async Task DescribeIntentAsync_OverlayPath_FromProperty()
    {
        var ctx = CreateContext(action: CreateAction(overlayPath: "overlays/prod"));

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.OverlayPath.ShouldBe("overlays/prod");
    }

    [Fact]
    public async Task DescribeIntentAsync_OverlayPath_DefaultsToCurrent()
    {
        var ctx = CreateContext();

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.OverlayPath.ShouldBe(".");
    }

    [Fact]
    public async Task DescribeIntentAsync_CustomKustomizePath_FromProperty()
    {
        var ctx = CreateContext(action: CreateAction(customKustomizePath: "/opt/kustomize/v5/kustomize"));

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.CustomKustomizePath.ShouldBe("/opt/kustomize/v5/kustomize");
    }

    [Fact]
    public async Task DescribeIntentAsync_CustomKustomizePath_DefaultsToEmpty()
    {
        var ctx = CreateContext();

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.CustomKustomizePath.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task DescribeIntentAsync_AdditionalArgs_FromProperty()
    {
        var ctx = CreateContext(action: CreateAction(additionalArgs: "--enable-helm --load-restrictor LoadRestrictionsNone"));

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.AdditionalArgs.ShouldBe("--enable-helm --load-restrictor LoadRestrictionsNone");
    }

    [Fact]
    public async Task DescribeIntentAsync_AdditionalArgs_DefaultsToEmpty()
    {
        var ctx = CreateContext();

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.AdditionalArgs.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task DescribeIntentAsync_ExplicitNamespace_UsedInIntent()
    {
        var ctx = CreateContext(action: CreateAction(namespaceValue: "staging"));

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Namespace.ShouldBe("staging");
    }

    [Fact]
    public async Task DescribeIntentAsync_NoNamespace_FallsBackToDefault()
    {
        var ctx = CreateContext();

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Namespace.ShouldBe("default");
    }

    [Fact]
    public async Task DescribeIntentAsync_ServerSideApplyDisabled_DefaultsFalse()
    {
        var ctx = CreateContext();

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ServerSideApply.ShouldBeFalse();
        intent.FieldManager.ShouldBe("squid-deploy");
        intent.ForceConflicts.ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeIntentAsync_ServerSideApplyEnabled_PropagatesFlags()
    {
        var ctx = CreateContext(action: CreateAction(
            serverSideApply: true,
            fieldManager: "squid-kustomize",
            forceConflicts: true));

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ServerSideApply.ShouldBeTrue();
        intent.FieldManager.ShouldBe("squid-kustomize");
        intent.ForceConflicts.ShouldBeTrue();
    }

    [Fact]
    public async Task DescribeIntentAsync_ServerSideApplyEnabled_NoFieldManager_UsesSquidDeployDefault()
    {
        var ctx = CreateContext(action: CreateAction(serverSideApply: true));

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ServerSideApply.ShouldBeTrue();
        intent.FieldManager.ShouldBe("squid-deploy");
    }

    [Fact]
    public async Task DescribeIntentAsync_YamlFiles_AreEmpty()
    {
        // Kustomize manifests are generated on the target at apply time — the intent
        // MUST NOT carry pre-rendered YAML. That's what distinguishes it from
        // KubernetesApplyIntent.
        var ctx = CreateContext();

        var intent = (KubernetesKustomizeIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Assets.ShouldBeEmpty();
    }

    [Fact]
    public async Task DescribeIntentAsync_PopulatesStepAndActionName()
    {
        var ctx = CreateContext(stepName: "Deploy Overlay", action: CreateAction(actionName: "apply-prod"));

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Deploy Overlay");
        intent.ActionName.ShouldBe("apply-prod");
    }

    [Fact]
    public async Task DescribeIntentAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(null!, CancellationToken.None));
    }
}
