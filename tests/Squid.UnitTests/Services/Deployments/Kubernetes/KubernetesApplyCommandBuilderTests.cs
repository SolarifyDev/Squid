using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesApplyCommandBuilderTests
{
    private static DeploymentActionDto CreateAction(Dictionary<string, string> properties = null)
    {
        var action = new DeploymentActionDto
        {
            Id = 1,
            Name = "test-action",
            ActionType = "Squid.KubernetesDeployRawYaml",
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (properties != null)
        {
            foreach (var kvp in properties)
                action.Properties.Add(new DeploymentActionPropertyDto { ActionId = 1, PropertyName = kvp.Key, PropertyValue = kvp.Value });
        }

        return action;
    }

    [Fact]
    public void Build_Default_PlainKubectlApply()
    {
        var result = KubernetesApplyCommandBuilder.Build("./inline-deployment.yaml", CreateAction(), ScriptSyntax.Bash);

        result.ShouldBe("kubectl apply -f \"./inline-deployment.yaml\"");
    }

    [Fact]
    public void Build_ServerSideApplyEnabled_ContainsServerSideFlag()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Kubernetes.ServerSideApply.Enabled"] = "True"
        });

        var result = KubernetesApplyCommandBuilder.Build("./deploy.yaml", action, ScriptSyntax.Bash);

        result.ShouldContain("--server-side");
    }

    [Fact]
    public void Build_ServerSideApplyEnabled_DefaultFieldManager_UsesSquidDeploy()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Kubernetes.ServerSideApply.Enabled"] = "True"
        });

        var result = KubernetesApplyCommandBuilder.Build("./deploy.yaml", action, ScriptSyntax.Bash);

        result.ShouldContain("--field-manager=squid-deploy");
    }

    [Fact]
    public void Build_ServerSideApplyEnabled_CustomFieldManager_UsesCustomValue()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Kubernetes.ServerSideApply.Enabled"] = "True",
            ["Squid.Action.Kubernetes.ServerSideApply.FieldManager"] = "my-controller"
        });

        var result = KubernetesApplyCommandBuilder.Build("./deploy.yaml", action, ScriptSyntax.Bash);

        result.ShouldContain("--field-manager=my-controller");
        result.ShouldNotContain("squid-deploy");
    }

    [Fact]
    public void Build_ForceConflictsEnabled_ContainsForceConflictsFlag()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Kubernetes.ServerSideApply.Enabled"] = "True",
            ["Squid.Action.Kubernetes.ServerSideApply.ForceConflicts"] = "True"
        });

        var result = KubernetesApplyCommandBuilder.Build("./deploy.yaml", action, ScriptSyntax.Bash);

        result.ShouldContain("--force-conflicts");
    }

    [Fact]
    public void Build_ForceConflictsWithoutServerSideApply_NoForceConflictsFlag()
    {
        var action = CreateAction(new Dictionary<string, string>
        {
            ["Squid.Action.Kubernetes.ServerSideApply.ForceConflicts"] = "True"
        });

        var result = KubernetesApplyCommandBuilder.Build("./deploy.yaml", action, ScriptSyntax.Bash);

        result.ShouldNotContain("--force-conflicts");
        result.ShouldNotContain("--server-side");
    }

    [Theory]
    [InlineData(ScriptSyntax.Bash)]
    [InlineData(ScriptSyntax.PowerShell)]
    public void Build_Syntax_ContainsKubectlApply(ScriptSyntax syntax)
    {
        var result = KubernetesApplyCommandBuilder.Build("./deploy.yaml", CreateAction(), syntax);

        result.ShouldStartWith("kubectl apply -f");
    }
}
