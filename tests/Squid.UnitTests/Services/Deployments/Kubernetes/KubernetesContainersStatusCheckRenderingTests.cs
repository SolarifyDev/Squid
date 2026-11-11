using System.Collections.Generic;
using System.Linq;
using Moq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Seam test for an imported "Deploy Kubernetes containers" step: Octopus action →
/// <see cref="OctopusKubernetesDeployContainersActionMapper"/> → real
/// <see cref="KubernetesContainersActionYamlGenerator"/> via <see cref="KubernetesYamlActionHandler"/>
/// → both Kubernetes renderers. Asserts the rendered status-check script waits on every
/// workload (step workload + custom resource documents) in the namespace each document is
/// applied into, with the imported DeploymentTimeout.
/// </summary>
public class KubernetesContainersStatusCheckRenderingTests
{
    private const string P = "Octopus.Action.KubernetesContainers.";
    private const string AllFeatures = "Octopus.Features.KubernetesService,Octopus.Features.KubernetesIngress,Octopus.Features.KubernetesConfigMap,Octopus.Features.KubernetesSecret,Octopus.Features.KubernetesCustomResource";
    private const string Containers = """[{ "Name": "web", "Image": "nginx:1.25", "Ports": [{ "key": "http", "value": "80", "option": "TCP" }] }]""";

    private const string CustomResourceYaml = """
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          labels:
            name: web
          name: web-exporter
          namespace: monitoring
        spec:
          replicas: 1
        ---
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: web-settings
        """;

    [Theory]
    [InlineData(CommunicationStyle.KubernetesApi)]
    [InlineData(CommunicationStyle.KubernetesAgent)]
    public async Task ImportedDeploymentWaitStep_WithCustomResource_WaitsOnEachDocumentInItsOwnNamespace(CommunicationStyle style)
    {
        var script = await RenderImportedStepAsync(style, "Deployment", ("Octopus.Action.Kubernetes.DeploymentTimeout", "420"), (P + "CustomResourceYaml", CustomResourceYaml));

        script.ShouldContain("kubectl apply -f \"./customresource.yaml\"");
        script.ShouldContain("kubectl rollout status \"deployment/web\" -n \"apps\" --timeout=420s");
        script.ShouldContain("kubectl rollout status \"deployment/web-exporter\" -n \"monitoring\" --timeout=420s");
        script.ShouldNotContain("deployment/web-exporter\" -n \"apps\"");
        script.ShouldNotContain("web-settings\" -n");
    }

    [Theory]
    [InlineData(CommunicationStyle.KubernetesApi, "Deployment", "kubectl rollout status \"deployment/web\" -n \"apps\" --timeout=300s")]
    [InlineData(CommunicationStyle.KubernetesApi, "StatefulSet", "kubectl rollout status \"statefulset/web\" -n \"apps\" --timeout=300s")]
    [InlineData(CommunicationStyle.KubernetesApi, "DaemonSet", "kubectl rollout status \"daemonset/web\" -n \"apps\" --timeout=300s")]
    [InlineData(CommunicationStyle.KubernetesApi, "Job", "kubectl wait --for=condition=complete \"job/web\" -n \"apps\" --timeout=300s")]
    [InlineData(CommunicationStyle.KubernetesAgent, "Deployment", "kubectl rollout status \"deployment/web\" -n \"apps\" --timeout=300s")]
    [InlineData(CommunicationStyle.KubernetesAgent, "StatefulSet", "kubectl rollout status \"statefulset/web\" -n \"apps\" --timeout=300s")]
    [InlineData(CommunicationStyle.KubernetesAgent, "DaemonSet", "kubectl rollout status \"daemonset/web\" -n \"apps\" --timeout=300s")]
    [InlineData(CommunicationStyle.KubernetesAgent, "Job", "kubectl wait --for=condition=complete \"job/web\" -n \"apps\" --timeout=300s")]
    public async Task ImportedDeploymentWaitStep_EveryWorkloadType_WaitsOnTheWorkloadAndTheCustomResource(CommunicationStyle style, string resourceType, string expectedWorkloadWait)
    {
        var script = await RenderImportedStepAsync(style, resourceType, (P + "CustomResourceYaml", CustomResourceYaml));

        script.ShouldContain(expectedWorkloadWait);
        script.ShouldContain("kubectl rollout status \"deployment/web-exporter\" -n \"monitoring\" --timeout=300s");
    }

    [Theory]
    [InlineData(CommunicationStyle.KubernetesApi)]
    [InlineData(CommunicationStyle.KubernetesAgent)]
    public async Task ImportedStep_DeploymentWaitWaitButResourceStatusCheckFalse_StillWaitsLikeOctopus(CommunicationStyle style)
    {
        var script = await RenderImportedStepAsync(style, "Deployment", ("Octopus.Action.Kubernetes.ResourceStatusCheck", "False"));

        script.ShouldContain("kubectl rollout status \"deployment/web\" -n \"apps\"");
    }

    [Theory]
    [InlineData(CommunicationStyle.KubernetesApi)]
    [InlineData(CommunicationStyle.KubernetesAgent)]
    public async Task ImportedStep_CustomResourceFeatureDisabled_NeitherAppliesNorWaitsOnIt(CommunicationStyle style)
    {
        var script = await RenderImportedStepAsync(style, "Deployment", ("Octopus.Action.EnabledFeatures", "Octopus.Features.KubernetesService"), (P + "CustomResourceYaml", CustomResourceYaml));

        script.ShouldNotContain("customresource.yaml");
        script.ShouldNotContain("web-exporter");
        script.ShouldContain("kubectl rollout status \"deployment/web\" -n \"apps\"");
    }

    private static async Task<string> RenderImportedStepAsync(CommunicationStyle style, string resourceType, params (string Key, string Value)[] overrides)
    {
        var action = ToDeploymentAction(MapImportedAction(resourceType, overrides));
        var intent = await DescribeIntentAsync(action).ConfigureAwait(false);

        return await RenderAsync(style, intent).ConfigureAwait(false);
    }

    private static CreateOrUpdateDeploymentActionModel MapImportedAction(string resourceType, (string Key, string Value)[] overrides)
    {
        var source = new OctopusDeploymentActionDto { Id = "Actions-1", Name = "Deploy web", ActionType = "Octopus.KubernetesDeployContainers", Properties = new() };

        source.Properties["Octopus.Action.EnabledFeatures"] = AllFeatures;
        source.Properties[P + "DeploymentResourceType"] = resourceType;
        source.Properties[P + "DeploymentName"] = "web";
        source.Properties[P + "Namespace"] = "apps";
        source.Properties[P + "Containers"] = Containers;
        source.Properties[P + "DeploymentWait"] = "Wait";

        foreach (var (key, value) in overrides) source.Properties[key] = value;

        return new OctopusKubernetesDeployContainersActionMapper().Map(source, new OctopusImportActionMappingContext(new OctopusImportIdMap(), 42)).Action;
    }

    private static DeploymentActionDto ToDeploymentAction(CreateOrUpdateDeploymentActionModel model)
        => new()
        {
            Id = 7,
            Name = model.Name,
            ActionType = model.ActionType,
            Properties = model.Properties.Select(p => new DeploymentActionPropertyDto { ActionId = 7, PropertyName = p.PropertyName, PropertyValue = p.PropertyValue }).ToList()
        };

    private static async Task<KubernetesApplyIntent> DescribeIntentAsync(DeploymentActionDto action)
    {
        IActionHandler handler = new KubernetesYamlActionHandler(new IActionYamlGenerator[] { new KubernetesContainersActionYamlGenerator() });

        var ctx = new ActionExecutionContext { Step = new DeploymentStepDto { Name = "Deploy web" }, Action = action, SelectedPackages = new List<SelectedPackageDto>(), Variables = new List<VariableDto>() };

        return (KubernetesApplyIntent)await handler.DescribeIntentAsync(ctx, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<string> RenderAsync(CommunicationStyle style, KubernetesApplyIntent intent)
    {
        if (style == CommunicationStyle.KubernetesAgent)
            return (await new KubernetesAgentIntentRenderer().RenderAsync(intent, NewContext(style), CancellationToken.None).ConfigureAwait(false)).ScriptBody;

        string captured = null;
        var builder = new Mock<IKubernetesApiContextScriptBuilder>();
        builder.Setup(b => b.WrapWithContext(It.IsAny<string>(), It.IsAny<ScriptContext>(), It.IsAny<string>())).Callback<string, ScriptContext, string>((body, _, _) => captured = body).Returns("wrapped");

        await new KubernetesApiIntentRenderer(builder.Object).RenderAsync(intent, NewContext(style), CancellationToken.None).ConfigureAwait(false);

        return captured;
    }

    private static IntentRenderContext NewContext(CommunicationStyle style)
    {
        var variables = new List<VariableDto>();

        return new IntentRenderContext
        {
            Target = new DeploymentTargetContext { Machine = new Machine { Id = 1, Name = "m1" }, CommunicationStyle = style, EndpointContext = new EndpointContext() },
            Step = new DeploymentStepDto { Name = "Deploy web" },
            EffectiveVariables = variables,
            VariableDictionary = VariableDictionaryFactory.Create(variables),
            ServerTaskId = 42,
            ReleaseVersion = "1.0.0",
            PackageReferences = new List<PackageAcquisitionResult>()
        };
    }
}
