using System.Collections.Generic;
using System.Text;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesDeployIngressActionHandlerTests
{
    private readonly KubernetesDeployIngressActionHandler _handler = new();

    private static DeploymentActionDto CreateAction(
        string actionType = "Squid.KubernetesDeployIngress",
        string ingressName = "my-ingress",
        string namespaceName = "default",
        string rulesJson = "[{\"host\":\"example.com\"}]",
        string annotationsJson = null,
        string tlsJson = null,
        string ingressClassName = null)
    {
        var action = new DeploymentActionDto
        {
            ActionType = actionType,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (ingressName != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.IngressName",
                PropertyValue = ingressName
            });
        }

        if (namespaceName != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.Kubernetes.Namespace",
                PropertyValue = namespaceName
            });
        }

        if (rulesJson != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.IngressRules",
                PropertyValue = rulesJson
            });
        }

        if (annotationsJson != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.IngressAnnotations",
                PropertyValue = annotationsJson
            });
        }

        if (tlsJson != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.IngressTlsCertificates",
                PropertyValue = tlsJson
            });
        }

        if (ingressClassName != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.IngressClassName",
                PropertyValue = ingressClassName
            });
        }

        return action;
    }

    private static ActionExecutionContext CreateContext(DeploymentActionDto action) => new()
    {
        Action = action
    };

    // === CanHandle Tests ===

    [Fact]
    public void CanHandle_MatchingActionType_ReturnsTrue()
    {
        var action = CreateAction();
        _handler.CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_CaseInsensitive_ReturnsTrue()
    {
        var action = CreateAction(actionType: "squid.kubernetesdeployingress");
        _handler.CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_DifferentActionType_ReturnsFalse()
    {
        var action = CreateAction(actionType: "Squid.KubernetesRunScript");
        _handler.CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullAction_ReturnsFalse()
    {
        _handler.CanHandle(null).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = null };
        _handler.CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void ActionType_ReturnsExpectedValue()
    {
        _handler.ActionType.ShouldBe(DeploymentActionType.KubernetesDeployIngress);
    }

    // === PrepareAsync Tests ===

    [Fact]
    public async Task PrepareAsync_WithRules_ReturnsDirectScriptMode()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
    }

    [Fact]
    public async Task PrepareAsync_WithRules_ReturnsApplyPolicy()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ContextPreparationPolicy.ShouldBe(ContextPreparationPolicy.Apply);
    }

    [Fact]
    public async Task PrepareAsync_WithRules_ReturnsIngressYamlFile()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldContainKey("ingress.yaml");
    }

    [Fact]
    public async Task PrepareAsync_WithRules_ScriptContainsKubectlApply()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ScriptBody.ShouldContain("kubectl apply -f");
        result.ScriptBody.ShouldContain("ingress.yaml");
    }

    [Fact]
    public async Task PrepareAsync_CalamariCommand_IsNull()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.CalamariCommand.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_Syntax_IsBash()
    {
        var action = CreateAction();
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.Syntax.ShouldBe(ScriptSyntax.Bash);
    }

    [Fact]
    public async Task PrepareAsync_NoRules_ReturnsNull()
    {
        var action = CreateAction(rulesJson: null);
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_EmptyRules_ReturnsNull()
    {
        var action = CreateAction(rulesJson: "");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task PrepareAsync_FullScenario_GeneratesCorrectYaml()
    {
        var action = CreateAction(
            ingressName: "web-ingress",
            namespaceName: "production",
            rulesJson: "[{\"host\":\"app.example.com\"}]",
            annotationsJson: "{\"nginx.ingress.kubernetes.io/rewrite-target\": \"/\"}",
            tlsJson: "[{\"hosts\":[\"app.example.com\"],\"secretName\":\"tls-secret\"}]",
            ingressClassName: "nginx");
        var ctx = CreateContext(action);

        var result = await _handler.PrepareAsync(ctx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Files.ShouldContainKey("ingress.yaml");

        var yaml = Encoding.UTF8.GetString(result.Files["ingress.yaml"]);
        yaml.ShouldContain("kind: Ingress");
        yaml.ShouldContain("name: web-ingress");
        yaml.ShouldContain("namespace: production");
        yaml.ShouldContain("ingressClassName: nginx");
        yaml.ShouldContain("app.example.com");
        yaml.ShouldContain("tls-secret");
    }
}
