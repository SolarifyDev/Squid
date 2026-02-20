using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesAgentPipelineBehaviorTests
{
    // === ParseCommunicationStyle ===

    [Theory]
    [InlineData("{\"CommunicationStyle\":\"KubernetesAgent\"}", "KubernetesAgent")]
    [InlineData("{\"CommunicationStyle\":\"KubernetesApi\"}", "KubernetesApi")]
    [InlineData("{\"communicationStyle\":\"KubernetesApi\"}", "KubernetesApi")]
    [InlineData("{}", null)]
    [InlineData("invalid", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseCommunicationStyle_ReturnsExpected(string endpointJson, string expected)
    {
        var result = ParseCommunicationStyle(endpointJson);

        result.ShouldBe(expected);
    }

    // === Script Context Wrapping ===

    [Fact]
    public void KubernetesApiScriptContextWrapper_CannotWrapAgent()
    {
        var builder = new Mock<IKubernetesApiContextScriptBuilder>();
        var wrapper = new KubernetesApiScriptContextWrapper(builder.Object);

        wrapper.CanWrap("KubernetesAgent").ShouldBeFalse();
    }

    [Fact]
    public void NoScriptContextWrapper_CanWrapAgent()
    {
        var wrappers = new List<IScriptContextWrapper>
        {
            new KubernetesApiScriptContextWrapper(new Mock<IKubernetesApiContextScriptBuilder>().Object)
        };

        var match = wrappers.FirstOrDefault(w => w.CanWrap("KubernetesAgent"));

        match.ShouldBeNull();
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

    /// <summary>
    /// Mirrors the static ParseCommunicationStyle from DeploymentTaskExecutor.Prepare.cs
    /// to test the parsing logic without needing the full executor.
    /// </summary>
    private static string ParseCommunicationStyle(string endpointJson)
    {
        if (string.IsNullOrEmpty(endpointJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(endpointJson);

            if (doc.RootElement.TryGetProperty("CommunicationStyle", out var prop))
                return prop.GetString();

            if (doc.RootElement.TryGetProperty("communicationStyle", out var prop2))
                return prop2.GetString();

            return null;
        }
        catch
        {
            return null;
        }
    }
}
