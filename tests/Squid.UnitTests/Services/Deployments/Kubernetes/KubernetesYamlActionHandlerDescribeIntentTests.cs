using System.Collections.Generic;
using System.Linq;
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
/// Phase 9c.2 — verifies that <see cref="KubernetesYamlActionHandler"/> overrides
/// <c>DescribeIntentAsync</c> to emit a <see cref="KubernetesApplyIntent"/> directly,
/// with a stable semantic name (<c>k8s-apply</c>), YAML files sourced from the matching
/// <see cref="IActionYamlGenerator"/>, the resolved namespace, and non-YAML assets
/// (e.g. <c>.sh</c> helper files) stripped. The legacy <c>PrepareAsync</c> path is
/// preserved until Phase 9g flips the pipeline.
/// </summary>
public class KubernetesYamlActionHandlerDescribeIntentTests
{
    private static Mock<IActionYamlGenerator> CreateMockGenerator(Dictionary<string, byte[]> yamlFiles = null)
    {
        var mock = new Mock<IActionYamlGenerator>();
        mock.Setup(g => g.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(true);
        mock.Setup(g => g.GenerateAsync(
                It.IsAny<DeploymentStepDto>(),
                It.IsAny<DeploymentActionDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(yamlFiles ?? new Dictionary<string, byte[]>());
        return mock;
    }

    private static DeploymentActionDto CreateAction(
        string actionName = "deploy-containers",
        string namespaceValue = null)
    {
        var action = new DeploymentActionDto
        {
            Name = actionName,
            ActionType = SpecialVariables.ActionTypes.KubernetesDeployContainers,
            Properties = new List<DeploymentActionPropertyDto>()
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
        string stepName = "Deploy Containers",
        DeploymentActionDto action = null) => new()
    {
        Step = new DeploymentStepDto { Name = stepName },
        Action = action ?? CreateAction(),
        SelectedPackages = new List<SelectedPackageDto>(),
        Variables = new List<VariableDto>()
    };

    private static KubernetesYamlActionHandler CreateHandler(Dictionary<string, byte[]> yamlFiles = null)
    {
        var generator = CreateMockGenerator(yamlFiles);
        return new KubernetesYamlActionHandler(new[] { generator.Object });
    }

    [Fact]
    public async Task DescribeIntentAsync_ReturnsKubernetesApplyIntent()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\nkind: Deployment\n")
        };
        var handler = CreateHandler(files);
        var ctx = CreateContext();

        var intent = await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ShouldBeOfType<KubernetesApplyIntent>();
    }

    [Fact]
    public async Task DescribeIntentAsync_NameIsK8sApply()
    {
        var handler = CreateHandler(new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\n")
        });
        var ctx = CreateContext();

        var intent = await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldBe("k8s-apply");
    }

    [Fact]
    public async Task DescribeIntentAsync_DoesNotUseLegacyName()
    {
        var handler = CreateHandler(new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\n")
        });
        var ctx = CreateContext();

        var intent = await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Name.ShouldNotStartWith("legacy:");
    }

    [Fact]
    public async Task DescribeIntentAsync_YamlFilesFromGenerator()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\nkind: Deployment\n"),
            ["service.yaml"] = Encoding.UTF8.GetBytes("apiVersion: v1\nkind: Service\n")
        };
        var handler = CreateHandler(files);
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.YamlFiles.Count.ShouldBe(2);
        intent.YamlFiles.Select(f => f.RelativePath).ShouldContain("deployment.yaml");
        intent.YamlFiles.Select(f => f.RelativePath).ShouldContain("service.yaml");
    }

    [Fact]
    public async Task DescribeIntentAsync_NonYamlFiles_Excluded()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\n"),
            ["wait-helper.sh"] = Encoding.UTF8.GetBytes("#!/bin/bash\nkubectl wait\n")
        };
        var handler = CreateHandler(files);
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.YamlFiles.Count.ShouldBe(1);
        intent.YamlFiles[0].RelativePath.ShouldBe("deployment.yaml");
    }

    [Fact]
    public async Task DescribeIntentAsync_AssetsMatchYamlFiles()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\n"),
            ["service.yaml"] = Encoding.UTF8.GetBytes("apiVersion: v1\n")
        };
        var handler = CreateHandler(files);
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Assets.Count.ShouldBe(intent.YamlFiles.Count);
        intent.Assets.Select(a => a.RelativePath)
            .ShouldBe(intent.YamlFiles.Select(f => f.RelativePath), ignoreOrder: true);
    }

    [Fact]
    public async Task DescribeIntentAsync_ExplicitNamespace_UsedInIntent()
    {
        var handler = CreateHandler(new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\n")
        });
        var ctx = CreateContext(action: CreateAction(namespaceValue: "production"));

        var intent = (KubernetesApplyIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Namespace.ShouldBe("production");
    }

    [Fact]
    public async Task DescribeIntentAsync_NoNamespace_FallsBackToDefault()
    {
        var handler = CreateHandler(new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\n")
        });
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.Namespace.ShouldBe("default");
    }

    [Fact]
    public async Task DescribeIntentAsync_PopulatesStepAndActionName()
    {
        var handler = CreateHandler(new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\n")
        });
        var ctx = CreateContext(stepName: "Apply Web", action: CreateAction(actionName: "deploy-web"));

        var intent = await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.StepName.ShouldBe("Apply Web");
        intent.ActionName.ShouldBe("deploy-web");
    }

    [Fact]
    public async Task DescribeIntentAsync_ServerSideApply_DefaultsToFalse()
    {
        var handler = CreateHandler(new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\n")
        });
        var ctx = CreateContext();

        var intent = (KubernetesApplyIntent)await ((IActionHandler)handler).DescribeIntentAsync(ctx, CancellationToken.None);

        intent.ServerSideApply.ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeIntentAsync_NullContext_Throws()
    {
        var handler = CreateHandler();

        await Should.ThrowAsync<ArgumentNullException>(
            () => ((IActionHandler)handler).DescribeIntentAsync(null!, CancellationToken.None));
    }
}
