using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesAgentEndpointVariableContributorTests
{
    private readonly KubernetesAgentEndpointVariableContributor _contributor = new();

    private static string MakeEndpointJson(string ns = "production") =>
        JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", Namespace = ns });

    // === CanHandle ===

    [Theory]
    [InlineData("KubernetesAgent", true)]
    [InlineData("kubernetesagent", true)]
    [InlineData("KubernetesApi", false)]
    [InlineData("Ssh", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanHandle_ReturnsExpected(string communicationStyle, bool expected)
    {
        _contributor.CanHandle(communicationStyle).ShouldBe(expected);
    }

    // === ParseAccountId ===

    [Fact]
    public void ParseAccountId_AlwaysReturnsNull()
    {
        var json = MakeEndpointJson();

        _contributor.ParseAccountId(json).ShouldBeNull();
    }

    // === ContributeVariables — count & names ===

    [Fact]
    public void ContributeVariables_ValidEndpoint_Returns3Variables()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), null);

        vars.Count.ShouldBe(3);
    }

    [Fact]
    public void ContributeVariables_AllExpectedVariableNamesPresent()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), null);
        var names = vars.Select(v => v.Name).ToList();

        names.ShouldContain("Squid.Action.Kubernetes.Namespace");
        names.ShouldContain("Squid.Action.Script.SuppressEnvironmentLogging");
        names.ShouldContain("SquidPrintEvaluatedVariables");
    }

    // === ContributeVariables — namespace ===

    [Fact]
    public void ContributeVariables_Namespace_MappedCorrectly()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(ns: "staging"), null);

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.Namespace" && v.Value == "staging");
    }

    [Fact]
    public void ContributeVariables_NullNamespace_DefaultsToDefault()
    {
        var json = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", Namespace = (string)null });

        var vars = _contributor.ContributeVariables(json, null);

        vars.ShouldContain(v => v.Name == "Squid.Action.Kubernetes.Namespace" && v.Value == "default");
    }

    // === ContributeVariables — static/fixed variables ===

    [Fact]
    public void ContributeVariables_SuppressEnvironmentLogging_AlwaysFalse()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), null);

        vars.ShouldContain(v => v.Name == "Squid.Action.Script.SuppressEnvironmentLogging" && v.Value == "False");
    }

    [Fact]
    public void ContributeVariables_PrintEvaluatedVariables_AlwaysTrue()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), null);

        vars.ShouldContain(v => v.Name == "SquidPrintEvaluatedVariables" && v.Value == "True");
    }

    // === ContributeVariables — no account credential variables ===

    [Fact]
    public void ContributeVariables_NoAccountCredentialVariables()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), CreateTokenAccount());
        var names = vars.Select(v => v.Name).ToList();

        names.ShouldNotContain("Squid.Account.AccountType");
        names.ShouldNotContain("Squid.Account.Token");
        names.ShouldNotContain("Squid.Account.Username");
        names.ShouldNotContain("Squid.Account.Password");
        names.ShouldNotContain("Squid.Account.AccessKey");
        names.ShouldNotContain("Squid.Account.SecretKey");
        names.ShouldNotContain("Squid.Account.ClientCertificateData");
        names.ShouldNotContain("Squid.Account.ClientCertificateKeyData");
    }

    // === ContributeVariables — null account still works ===

    [Fact]
    public void ContributeVariables_NullAccount_StillReturns3Variables()
    {
        var vars = _contributor.ContributeVariables(MakeEndpointJson(), null);

        vars.Count.ShouldBe(3);
    }

    // === ContributeVariables — bad input ===

    [Fact]
    public void ContributeVariables_InvalidJson_ReturnsEmpty()
    {
        var vars = _contributor.ContributeVariables("not-json", null);

        vars.ShouldBeEmpty();
    }

    [Fact]
    public void ContributeVariables_EmptyJson_ReturnsEmpty()
    {
        var vars = _contributor.ContributeVariables(string.Empty, null);

        vars.ShouldBeEmpty();
    }

    [Fact]
    public void ContributeVariables_NullJson_ReturnsEmpty()
    {
        var vars = _contributor.ContributeVariables(null, null);

        vars.ShouldBeEmpty();
    }

    // === ContributeAdditionalVariablesAsync ===

    [Fact]
    public async Task ContributeAdditionalVariablesAsync_UsesDefaultInterface_ReturnsEmpty()
    {
        var snapshot = new DeploymentProcessSnapshotDto
        {
            Id = 1, OriginalProcessId = 1, Version = 1,
            Data = new DeploymentProcessSnapshotDataDto
            {
                StepSnapshots = new List<DeploymentStepSnapshotDataDto>()
            }
        };

        var release = new Release { Version = "1.0.0" };

        IEndpointVariableContributor contributor = _contributor;

        var vars = await contributor.ContributeAdditionalVariablesAsync(snapshot, release, CancellationToken.None);

        vars.ShouldBeEmpty();
    }

    private static DeploymentAccount CreateTokenAccount() => new()
    {
        AccountType = AccountType.Token,
        Token = "test-token-123"
    };
}
