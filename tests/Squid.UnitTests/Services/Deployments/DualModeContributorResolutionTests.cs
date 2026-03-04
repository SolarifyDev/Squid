using System;
using System.Collections.Generic;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments;

public class TransportRegistryTests
{
    // ========== CommunicationStyleParser ==========

    [Theory]
    [InlineData("KubernetesApi", CommunicationStyle.KubernetesApi)]
    [InlineData("kubernetesapi", CommunicationStyle.KubernetesApi)]
    [InlineData("KUBERNETESAPI", CommunicationStyle.KubernetesApi)]
    [InlineData("KubernetesAgent", CommunicationStyle.KubernetesAgent)]
    [InlineData("kubernetesagent", CommunicationStyle.KubernetesAgent)]
    public void Parse_KnownStyle_ReturnsMappedEnum(string styleValue, CommunicationStyle expected)
    {
        var json = MakeEndpointJson(styleValue);

        CommunicationStyleParser.Parse(json).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Ssh")]
    [InlineData("Docker")]
    [InlineData("")]
    public void Parse_UnknownStyle_ReturnsUnknown(string styleValue)
    {
        var json = MakeEndpointJson(styleValue);

        CommunicationStyleParser.Parse(json).ShouldBe(CommunicationStyle.Unknown);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_MissingOrInvalidInput_ReturnsUnknown(string input)
    {
        CommunicationStyleParser.Parse(input).ShouldBe(CommunicationStyle.Unknown);
    }

    [Fact]
    public void Parse_LowercasePropertyName_ReturnsMappedEnum()
    {
        var json = "{\"communicationStyle\": \"KubernetesApi\"}";

        CommunicationStyleParser.Parse(json).ShouldBe(CommunicationStyle.KubernetesApi);
    }

    // ========== TransportRegistry ==========

    [Theory]
    [InlineData(CommunicationStyle.KubernetesApi)]
    [InlineData(CommunicationStyle.KubernetesAgent)]
    public void Resolve_RegisteredStyle_ReturnsCorrectTransport(CommunicationStyle style)
    {
        var transport = new StubTransport(style);
        var registry = new TransportRegistry(new[] { transport });

        registry.Resolve(style).ShouldBe(transport);
    }

    [Fact]
    public void Resolve_UnknownStyle_ReturnsNull()
    {
        var registry = new TransportRegistry(new[] { new StubTransport(CommunicationStyle.KubernetesApi) });

        registry.Resolve(CommunicationStyle.Unknown).ShouldBeNull();
    }

    [Fact]
    public void Resolve_UnregisteredStyle_ReturnsNull()
    {
        var registry = new TransportRegistry(new[] { new StubTransport(CommunicationStyle.KubernetesApi) });

        registry.Resolve(CommunicationStyle.KubernetesAgent).ShouldBeNull();
    }

    [Fact]
    public void Constructor_DuplicateStyle_ThrowsArgumentException()
    {
        var transports = new[]
        {
            new StubTransport(CommunicationStyle.KubernetesApi),
            new StubTransport(CommunicationStyle.KubernetesApi)
        };

        Should.Throw<ArgumentException>(() => new TransportRegistry(transports));
    }

    // ========== Contributor behavior ==========

    [Fact]
    public void ApiContributor_ParseResourceReferences_ReturnsId()
    {
        var contributor = new KubernetesApiEndpointVariableContributor();
        var json = JsonSerializer.Serialize(new KubernetesApiEndpointDto
        {
            ResourceReferences = new List<EndpointResourceReference>
            {
                new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 42 }
            }
        });

        contributor.ParseResourceReferences(json).FindFirst(EndpointResourceType.AuthenticationAccount).ShouldBe(42);
    }

    [Fact]
    public void AgentContributor_ParseResourceReferences_ReturnsNull()
    {
        var contributor = new KubernetesAgentEndpointVariableContributor();

        var refs = contributor.ParseResourceReferences("{}");
        refs.FindFirst(EndpointResourceType.AuthenticationAccount).ShouldBeNull();
    }

    [Theory]
    [InlineData("KubernetesApi", 9)]
    [InlineData("KubernetesAgent", 3)]
    public void ContributeVariables_CorrectCount(string style, int expectedCount)
    {
        var json = MakeEndpointJson(style);
        var accountType = style == "KubernetesApi" ? Message.Enums.AccountType.Token : (Message.Enums.AccountType?)null;
        var credentialsJson = style == "KubernetesApi"
            ? JsonSerializer.Serialize(new TokenCredentials { Token = "t" })
            : null;

        IEndpointVariableContributor contributor = style == "KubernetesApi"
            ? new KubernetesApiEndpointVariableContributor()
            : new KubernetesAgentEndpointVariableContributor();

        var ctx = new EndpointContext { EndpointJson = json };

        if (accountType.HasValue && credentialsJson != null)
            ctx.SetAccountData(accountType.Value, credentialsJson);

        contributor.ContributeVariables(ctx).Count.ShouldBe(expectedCount);
    }

    // ========== DeploymentTargetContext ==========

    [Fact]
    public void TargetContext_AgentMode_AccountRemainsNull()
    {
        var tc = new DeploymentTargetContext
        {
            CommunicationStyle = CommunicationStyle.KubernetesAgent,
            Transport = new StubTransport(CommunicationStyle.KubernetesAgent,
                variables: new KubernetesAgentEndpointVariableContributor())
        };

        var refs = tc.Transport.Variables.ParseResourceReferences("{}");

        refs.FindFirst(EndpointResourceType.AuthenticationAccount).ShouldBeNull();
        tc.EndpointContext.GetAccountData().ShouldBeNull();
    }

    // ========== Helpers ==========

    private static string MakeEndpointJson(string communicationStyle) =>
        JsonSerializer.Serialize(new { CommunicationStyle = communicationStyle });

    private sealed class StubTransport : IDeploymentTransport
    {
        public CommunicationStyle CommunicationStyle { get; }
        public IEndpointVariableContributor Variables { get; }
        public IScriptContextWrapper ScriptWrapper => null;
        public IExecutionStrategy Strategy => null;
        public ExecutionLocation ExecutionLocation => ExecutionLocation.Unspecified;
        public ExecutionBackend ExecutionBackend => ExecutionBackend.Unspecified;
        public bool RequiresContextPreparationForPackagedPayload => false;

        public StubTransport(CommunicationStyle style, IEndpointVariableContributor variables = null)
        {
            CommunicationStyle = style;
            Variables = variables;
        }
    }
}
