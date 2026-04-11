using System.Collections.Generic;
using System.Linq;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Phase 9c.4 — verifies that <see cref="KubernetesDeployIngressActionHandler"/> overrides
/// <c>DescribeIntentAsync</c> and emits a <see cref="KubernetesApplyIntent"/> directly, with
/// a stable semantic name (<c>k8s-apply</c>) and the generated Ingress YAML carried as a
/// single <c>ingress.yaml</c> asset. Invalid/unconfigured actions produce an intent with an
/// empty <c>YamlFiles</c> collection instead of a null result. Legacy <c>PrepareAsync</c>
/// is preserved until Phase 9g.
/// </summary>
public class KubernetesDeployIngressActionHandlerDescribeIntentTests
{
    private readonly KubernetesDeployIngressActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(
        string actionName = "deploy-ingress",
        string ingressName = "my-ingress",
        string rulesJson = """[{"host":"example.com","paths":[{"path":"/","pathType":"Prefix","serviceName":"web","servicePort":80}]}]""",
        string namespaceValue = null)
    {
        var action = new DeploymentActionDto
        {
            Name = actionName,
            ActionType = SpecialVariables.ActionTypes.KubernetesDeployIngress,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (ingressName != null)
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.IngressName",
                PropertyValue = ingressName
            });

        if (rulesJson != null)
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.IngressRules",
                PropertyValue = rulesJson
            });

        if (namespaceValue != null)
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.Namespace",
                PropertyValue = namespaceValue
            });

        return action;
    }

    private static ActionExecutionContext CreateContext(
        string stepName = "Apply Ingress",
        DeploymentActionDto action = null) => new()
    {
        Step = new DeploymentStepDto { Name = stepName },
        Action = action ?? CreateAction()
    };

    [Fact]
    public async Task DescribeIntentAsync_ReturnsKubernetesApplyIntent()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ShouldBeOfType<KubernetesApplyIntent>();
    }

    [Fact]
    public async Task DescribeIntentAsync_NameIsK8sApply()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldBe("k8s-apply");
    }

    [Fact]
    public async Task DescribeIntentAsync_DoesNotUseLegacyName()
    {
        var ctx = CreateContext();

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldNotStartWith("legacy:");
    }

    [Fact]
    public async Task DescribeIntentAsync_ValidIngress_YieldsSingleIngressYamlFile()
    {
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.YamlFiles.Count.ShouldBe(1);
        intent.YamlFiles[0].RelativePath.ShouldBe("ingress.yaml");
    }

    [Fact]
    public async Task DescribeIntentAsync_YamlContent_HasKindIngress()
    {
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(intent.YamlFiles[0].Content);
        yaml.ShouldContain("kind: Ingress");
        yaml.ShouldContain("name: \"my-ingress\"");
    }

    [Fact]
    public async Task DescribeIntentAsync_AssetsMatchYamlFiles()
    {
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Assets.Count.ShouldBe(intent.YamlFiles.Count);
        intent.Assets.Select(a => a.RelativePath)
            .ShouldBe(intent.YamlFiles.Select(f => f.RelativePath), ignoreOrder: true);
    }

    [Fact]
    public async Task DescribeIntentAsync_MissingRules_ProducesEmptyYamlFiles()
    {
        var ctx = CreateContext(action: CreateAction(rulesJson: null));

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.YamlFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task DescribeIntentAsync_EmptyRules_ProducesEmptyYamlFiles()
    {
        var ctx = CreateContext(action: CreateAction(rulesJson: "[]"));

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.YamlFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task DescribeIntentAsync_ExplicitNamespace_UsedInIntent()
    {
        var ctx = CreateContext(action: CreateAction(namespaceValue: "production"));

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Namespace.ShouldBe("production");
    }

    [Fact]
    public async Task DescribeIntentAsync_NoNamespace_FallsBackToDefault()
    {
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Namespace.ShouldBe("default");
    }

    [Fact]
    public async Task DescribeIntentAsync_ServerSideApply_DefaultsToFalse()
    {
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ServerSideApply.ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeIntentAsync_PopulatesStepAndActionName()
    {
        var ctx = CreateContext(stepName: "Apply Ingress Prod", action: CreateAction(actionName: "prod-ingress"));

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Apply Ingress Prod");
        intent.ActionName.ShouldBe("prod-ingress");
    }

    [Fact]
    public async Task DescribeIntentAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(null!, CancellationToken.None));
    }
}
