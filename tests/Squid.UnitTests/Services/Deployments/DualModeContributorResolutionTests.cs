using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments;

public class DualModeContributorResolutionTests
{
    private static readonly List<IEndpointVariableContributor> Contributors = new()
    {
        new KubernetesApiEndpointVariableContributor(),
        new KubernetesAgentEndpointVariableContributor()
    };

    private static readonly List<IExecutionStrategy> Strategies = new();

    private static string MakeEndpointJson(string communicationStyle) =>
        JsonSerializer.Serialize(new { CommunicationStyle = communicationStyle });

    // === Contributor Resolution ===

    [Theory]
    [InlineData("KubernetesApi", typeof(KubernetesApiEndpointVariableContributor))]
    [InlineData("KubernetesAgent", typeof(KubernetesAgentEndpointVariableContributor))]
    public void ContributorResolution_CorrectContributor(string style, System.Type expectedType)
    {
        var json = MakeEndpointJson(style);
        var communicationStyle = ParseCommunicationStyle(json);

        var resolved = Contributors.FirstOrDefault(c => c.CanHandle(communicationStyle));

        resolved.ShouldNotBeNull();
        resolved.ShouldBeOfType(expectedType);
    }

    [Fact]
    public void ContributorResolution_UnknownStyle_ReturnsNull()
    {
        var json = MakeEndpointJson("Ssh");
        var communicationStyle = ParseCommunicationStyle(json);

        var resolved = Contributors.FirstOrDefault(c => c.CanHandle(communicationStyle));

        resolved.ShouldBeNull();
    }

    [Fact]
    public void ContributorResolution_InvalidJson_ReturnsNull()
    {
        var communicationStyle = ParseCommunicationStyle("not-json");

        var resolved = Contributors.FirstOrDefault(c => c.CanHandle(communicationStyle));

        resolved.ShouldBeNull();
    }

    [Fact]
    public void ContributorResolution_EmptyJson_ReturnsNull()
    {
        var communicationStyle = ParseCommunicationStyle("{}");

        var resolved = Contributors.FirstOrDefault(c => c.CanHandle(communicationStyle));

        resolved.ShouldBeNull();
    }

    // === Account Loading Behavior ===

    [Fact]
    public void ApiMode_ParseAccountId_ReturnsAccountId()
    {
        var json = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesApi", AccountId = "42" });
        var contributor = Contributors.First(c => c.CanHandle("KubernetesApi"));

        var accountId = contributor.ParseAccountId(json);

        accountId.ShouldBe(42);
    }

    [Fact]
    public void AgentMode_ParseAccountId_ReturnsNull()
    {
        var json = MakeEndpointJson("KubernetesAgent");
        var contributor = Contributors.First(c => c.CanHandle("KubernetesAgent"));

        var accountId = contributor.ParseAccountId(json);

        accountId.ShouldBeNull();
    }

    // === Variable Count Difference ===

    [Theory]
    [InlineData("KubernetesApi", 15)]
    [InlineData("KubernetesAgent", 3)]
    public void ContributeVariables_CorrectCount(string style, int expectedCount)
    {
        var json = MakeEndpointJson(style);
        var contributor = Contributors.First(c => c.CanHandle(style));

        var account = style == "KubernetesApi"
            ? new DeploymentAccount { AccountType = Message.Enums.AccountType.Token, Token = "t" }
            : null;

        var vars = contributor.ContributeVariables(json, account);

        vars.Count.ShouldBe(expectedCount);
    }

    // === Target Context Population ===

    [Fact]
    public void TargetContext_AgentMode_AccountRemainsNull()
    {
        var tc = new DeploymentTargetContext
        {
            Machine = new Machine { Endpoint = MakeEndpointJson("KubernetesAgent") }
        };

        tc.EndpointJson = tc.Machine.Endpoint;
        tc.CommunicationStyle = ParseCommunicationStyle(tc.EndpointJson);
        tc.ResolvedContributor = Contributors.FirstOrDefault(c => c.CanHandle(tc.CommunicationStyle));

        var accountId = tc.ResolvedContributor?.ParseAccountId(tc.EndpointJson);

        accountId.ShouldBeNull();
        tc.Account.ShouldBeNull();
    }

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
