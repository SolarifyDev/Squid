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
    public void AgentWrapper_WrapsScriptWithNamespaceFromActionProperties()
    {
        var wrapper = new KubernetesAgentScriptContextWrapper();
        var ctx = new ScriptContext
        {
            Endpoint = new EndpointContext { EndpointJson = "{}" },
            Syntax = Message.Models.Deployments.Execution.ScriptSyntax.Bash,
            ActionProperties = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                ["Squid.Action.KubernetesContainers.Namespace"] = "production"
            }
        };
        var result = wrapper.WrapScript("kubectl get pods", ctx);

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
        var endpointVars = contributor.ContributeVariables(new EndpointContext { EndpointJson = json });

        tc.EndpointVariables.AddRange(endpointVars);

        tc.EndpointVariables.Count.ShouldBe(3);
    }

    [Fact]
    public void AgentContributes3Variables_ApiContributes9()
    {
        var agentContributor = new KubernetesAgentEndpointVariableContributor();
        var apiContributor = new KubernetesApiEndpointVariableContributor();

        var agentJson = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", Namespace = "default" });
        var apiJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://k8s:6443",
            Namespace = "default",
            SkipTlsVerification = "False"
        });

        var agentVars = agentContributor.ContributeVariables(new EndpointContext { EndpointJson = agentJson });
        var apiVars = apiContributor.ContributeVariables(new EndpointContext { EndpointJson = apiJson });

        agentVars.Count.ShouldBe(3);
        apiVars.Count.ShouldBe(9);
    }

    [Fact]
    public void AgentEndpointVariables_ContainExpectedNames()
    {
        var contributor = new KubernetesAgentEndpointVariableContributor();
        var json = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", Namespace = "default" });

        var vars = contributor.ContributeVariables(new EndpointContext { EndpointJson = json });
        var names = vars.Select(v => v.Name).ToList();

        names.ShouldContain("Squid.Action.Script.SuppressEnvironmentLogging");
        names.ShouldContain("SquidPrintEvaluatedVariables");
    }
}
