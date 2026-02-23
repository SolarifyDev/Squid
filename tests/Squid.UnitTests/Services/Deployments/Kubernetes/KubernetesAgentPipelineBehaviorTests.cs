using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesAgentPipelineBehaviorTests
{
    // === CommunicationStyleParser ===

    [Theory]
    [InlineData("{\"CommunicationStyle\":\"KubernetesAgent\"}", CommunicationStyle.KubernetesAgent)]
    [InlineData("{\"CommunicationStyle\":\"KubernetesApi\"}", CommunicationStyle.KubernetesApi)]
    [InlineData("{\"communicationStyle\":\"KubernetesApi\"}", CommunicationStyle.KubernetesApi)]
    [InlineData("{}", CommunicationStyle.Unknown)]
    [InlineData("invalid", CommunicationStyle.Unknown)]
    [InlineData("", CommunicationStyle.Unknown)]
    [InlineData(null, CommunicationStyle.Unknown)]
    public void ParseCommunicationStyle_ReturnsExpected(string endpointJson, CommunicationStyle expected)
    {
        CommunicationStyleParser.Parse(endpointJson).ShouldBe(expected);
    }

    // === Transport ScriptWrapper wiring ===

    [Fact]
    public void AgentWrapper_WrapsScriptWithNamespaceFromEndpointVariables()
    {
        var contributor = new KubernetesAgentEndpointVariableContributor();
        var json = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", Namespace = "production" });
        var endpointVars = contributor.ContributeVariables(json, null);

        var wrapper = new KubernetesAgentScriptContextWrapper();
        var result = wrapper.WrapScript(
            "kubectl get pods", json, null,
            Message.Models.Deployments.Execution.ScriptSyntax.Bash, endpointVars);

        result.ShouldContain("--namespace=\"production\"");
        result.ShouldContain("kubectl get pods");
    }

    // === Endpoint Variable Isolation ===

    [Fact]
    public void AgentEndpointVariables_StoredInTargetContext()
    {
        var contributor = new KubernetesAgentEndpointVariableContributor();
        var json = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", Namespace = "prod" });

        var tc = new DeploymentTargetContext();
        var endpointVars = contributor.ContributeVariables(json, null);

        tc.EndpointVariables.AddRange(endpointVars);

        tc.EndpointVariables.Count.ShouldBe(3);
    }

    [Fact]
    public void AgentContributes3Variables_ApiContributes15()
    {
        var agentContributor = new KubernetesAgentEndpointVariableContributor();
        var apiContributor = new KubernetesApiEndpointVariableContributor();

        var agentJson = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", Namespace = "default" });
        var apiJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://k8s:6443",
            Namespace = "default",
            SkipTlsVerification = "False",
            AccountId = (string)null,
            ClusterCertificate = (string)null
        });

        var agentVars = agentContributor.ContributeVariables(agentJson, null);
        var apiVars = apiContributor.ContributeVariables(apiJson, null);

        agentVars.Count.ShouldBe(3);
        apiVars.Count.ShouldBe(15);
    }

    [Fact]
    public void AgentEndpointVariables_ContainExpectedNames()
    {
        var contributor = new KubernetesAgentEndpointVariableContributor();
        var json = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", Namespace = "default" });

        var vars = contributor.ContributeVariables(json, null);
        var names = vars.Select(v => v.Name).ToList();

        names.ShouldContain("Squid.Action.Kubernetes.Namespace");
        names.ShouldContain("Squid.Action.Script.SuppressEnvironmentLogging");
        names.ShouldContain("SquidPrintEvaluatedVariables");
    }
}
