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
/// Phase 9c.4 — verifies that <see cref="KubernetesDeployServiceActionHandler"/> overrides
/// <c>DescribeIntentAsync</c> and emits a <see cref="KubernetesApplyIntent"/> directly, with
/// a stable semantic name (<c>k8s-apply</c>) and the generated Service YAML carried as a
/// single <c>service.yaml</c> asset. Invalid/unconfigured actions produce an intent with an
/// empty <c>YamlFiles</c> collection instead of a null result. Legacy <c>PrepareAsync</c>
/// is preserved until Phase 9g.
/// </summary>
public class KubernetesDeployServiceActionHandlerDescribeIntentTests
{
    private readonly KubernetesDeployServiceActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(
        string actionName = "deploy-service",
        string serviceName = "my-service",
        string servicePorts = """[{"name":"http","port":80,"targetPort":"8080"}]""",
        string namespaceValue = null)
    {
        var action = new DeploymentActionDto
        {
            Name = actionName,
            ActionType = SpecialVariables.ActionTypes.KubernetesDeployService,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (serviceName != null)
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.ServiceName",
                PropertyValue = serviceName
            });

        if (servicePorts != null)
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.ServicePorts",
                PropertyValue = servicePorts
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
        string stepName = "Apply Service",
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
    public async Task DescribeIntentAsync_ValidService_YieldsSingleServiceYamlFile()
    {
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.YamlFiles.Count.ShouldBe(1);
        intent.YamlFiles[0].RelativePath.ShouldBe("service.yaml");
    }

    [Fact]
    public async Task DescribeIntentAsync_YamlContent_HasKindService()
    {
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(intent.YamlFiles[0].Content);
        yaml.ShouldContain("kind: Service");
        yaml.ShouldContain("name: \"my-service\"");
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
    public async Task DescribeIntentAsync_MissingServiceName_ProducesEmptyYamlFiles()
    {
        var ctx = CreateContext(action: CreateAction(serviceName: null));

        var intent = (KubernetesApplyIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.YamlFiles.ShouldBeEmpty();
    }

    [Fact]
    public async Task DescribeIntentAsync_NoPorts_ProducesEmptyYamlFiles()
    {
        var ctx = CreateContext(action: CreateAction(servicePorts: "[]"));

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
        var ctx = CreateContext(stepName: "Apply Service Prod", action: CreateAction(actionName: "prod-service"));

        var intent = await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Apply Service Prod");
        intent.ActionName.ShouldBe("prod-service");
    }

    [Fact]
    public async Task DescribeIntentAsync_NullContext_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(null!, CancellationToken.None));
    }
}
