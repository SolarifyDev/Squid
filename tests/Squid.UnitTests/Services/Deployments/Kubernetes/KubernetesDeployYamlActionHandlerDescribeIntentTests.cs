using System.Collections.Generic;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Phase 9c.1 — verifies that <see cref="KubernetesDeployYamlActionHandler"/> overrides
/// <c>DescribeIntentAsync</c> to emit a <see cref="KubernetesApplyIntent"/> directly,
/// with a stable semantic name (<c>k8s-apply</c>) and the namespace resolved from the
/// action properties. The legacy <c>PrepareAsync</c> path still serves the pipeline
/// until Phase 9g.
/// </summary>
public class KubernetesDeployYamlActionHandlerDescribeIntentTests
{
    private readonly KubernetesDeployYamlActionHandler _handler = new();

    private static DeploymentActionDto CreateInlineYamlAction(
        string actionName = "deploy-yaml",
        string inlineYaml = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: test\n",
        string namespaceValue = null)
    {
        var action = new DeploymentActionDto
        {
            Name = actionName,
            ActionType = SpecialVariables.ActionTypes.KubernetesDeployRawYaml,
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = "Squid.Action.KubernetesYaml.InlineYaml", PropertyValue = inlineYaml }
            }
        };

        if (namespaceValue != null)
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.Namespace",
                PropertyValue = namespaceValue
            });

        return action;
    }

    private static ActionExecutionContext CreateContext(
        string stepName = "Deploy Web",
        DeploymentActionDto action = null) => new()
    {
        Step = new DeploymentStepDto { Name = stepName },
        Action = action ?? CreateInlineYamlAction(),
        SelectedPackages = new List<SelectedPackageDto>(),
        Variables = new List<VariableDto>()
    };

    [Fact]
    public async Task DescribeIntentAsync_InlineYaml_ReturnsKubernetesApplyIntent()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ShouldBeOfType<KubernetesApplyIntent>();
    }

    [Fact]
    public async Task DescribeIntentAsync_InlineYaml_NameIsK8sApply()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldBe("k8s-apply");
    }

    [Fact]
    public async Task DescribeIntentAsync_InlineYaml_DoesNotUseLegacyName()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldNotStartWith("legacy:");
    }

    [Fact]
    public async Task DescribeIntentAsync_InlineYaml_YamlFilesContainInlineDeployment()
    {
        var inline = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: my-cm\n";
        var ctx = CreateContext(action: CreateInlineYamlAction(inlineYaml: inline));

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.YamlFiles.Count.ShouldBe(1);
        intent.YamlFiles[0].RelativePath.ShouldBe("inline-deployment.yaml");
        Encoding.UTF8.GetString(intent.YamlFiles[0].Content).ShouldBe(inline);
    }

    [Fact]
    public async Task DescribeIntentAsync_InlineYaml_AssetsMatchYamlFiles()
    {
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Assets.Count.ShouldBe(intent.YamlFiles.Count);
        intent.Assets[0].RelativePath.ShouldBe("inline-deployment.yaml");
    }

    [Fact]
    public async Task DescribeIntentAsync_ExplicitNamespace_UsedInIntent()
    {
        var ctx = CreateContext(action: CreateInlineYamlAction(namespaceValue: "production"));

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Namespace.ShouldBe("production");
    }

    [Fact]
    public async Task DescribeIntentAsync_NoNamespace_FallsBackToDefault()
    {
        var ctx = CreateContext(action: CreateInlineYamlAction());

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Namespace.ShouldBe("default");
    }

    [Fact]
    public async Task DescribeIntentAsync_PopulatesStepAndActionName()
    {
        var ctx = CreateContext(stepName: "Apply Cluster State", action: CreateInlineYamlAction(actionName: "apply-web"));

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Apply Cluster State");
        intent.ActionName.ShouldBe("apply-web");
    }

    [Fact]
    public async Task DescribeIntentAsync_ServerSideApply_DefaultsToFalse()
    {
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ServerSideApply.ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeIntentAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(null!, CancellationToken.None));
    }
}
