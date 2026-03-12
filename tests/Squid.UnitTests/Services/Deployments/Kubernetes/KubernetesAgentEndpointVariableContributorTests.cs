using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Snapshots;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesAgentEndpointVariableContributorTests
{
    private readonly KubernetesAgentEndpointVariableContributor _contributor = new();

    private static string MakeEndpointJson(string ns = "production") =>
        JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", Namespace = ns });

    // === ParseResourceReferences ===

    [Fact]
    public void ParseResourceReferences_AlwaysReturnsEmpty()
    {
        var json = MakeEndpointJson();
        var refs = _contributor.ParseResourceReferences(json);

        refs.FindFirst(EndpointResourceType.AuthenticationAccount).ShouldBeNull();
        refs.FindFirst(EndpointResourceType.ClientCertificate).ShouldBeNull();
    }

    // === ContributeVariables — count & names ===

    [Fact]
    public void ContributeVariables_ValidEndpoint_Returns3Variables()
    {
        var vars = _contributor.ContributeVariables(new EndpointContext { EndpointJson = MakeEndpointJson() });

        vars.Count.ShouldBe(3);
    }

    [Fact]
    public void ContributeVariables_AllExpectedVariableNamesPresent()
    {
        var vars = _contributor.ContributeVariables(new EndpointContext { EndpointJson = MakeEndpointJson() });
        var names = vars.Select(v => v.Name).ToList();

        names.ShouldContain("Squid.Action.Script.SuppressEnvironmentLogging");
        names.ShouldContain("SquidPrintEvaluatedVariables");
    }

    // === ContributeVariables — static/fixed variables ===

    [Fact]
    public void ContributeVariables_SuppressEnvironmentLogging_AlwaysFalse()
    {
        var vars = _contributor.ContributeVariables(new EndpointContext { EndpointJson = MakeEndpointJson() });

        vars.ShouldContain(v => v.Name == "Squid.Action.Script.SuppressEnvironmentLogging" && v.Value == "False");
    }

    [Fact]
    public void ContributeVariables_PrintEvaluatedVariables_AlwaysTrue()
    {
        var vars = _contributor.ContributeVariables(new EndpointContext { EndpointJson = MakeEndpointJson() });

        vars.ShouldContain(v => v.Name == "SquidPrintEvaluatedVariables" && v.Value == "True");
    }

    // === ContributeVariables — no account credential variables ===

    [Fact]
    public void ContributeVariables_NoAccountCredentialVariables()
    {
        var ctx = new EndpointContext { EndpointJson = MakeEndpointJson() };
        ctx.SetAccountData(AccountType.Token, JsonSerializer.Serialize(new Squid.Message.Models.Deployments.Account.TokenCredentials { Token = "test-token-123" }));
        var vars = _contributor.ContributeVariables(ctx);
        var names = vars.Select(v => v.Name).ToList();

        names.ShouldNotContain("Squid.Account.AccountType");
        names.ShouldNotContain("Squid.Account.CredentialsJson");
    }

    // === ContributeVariables — null account still works ===

    [Fact]
    public void ContributeVariables_NullAccount_StillReturns3Variables()
    {
        var vars = _contributor.ContributeVariables(new EndpointContext { EndpointJson = MakeEndpointJson() });

        vars.Count.ShouldBe(3);
    }

    // === ContributeVariables — bad input ===

    [Fact]
    public void ContributeVariables_InvalidJson_ReturnsEmpty()
    {
        var vars = _contributor.ContributeVariables(new EndpointContext { EndpointJson = "not-json" });

        vars.ShouldBeEmpty();
    }

    [Fact]
    public void ContributeVariables_EmptyJson_ReturnsEmpty()
    {
        var vars = _contributor.ContributeVariables(new EndpointContext { EndpointJson = string.Empty });

        vars.ShouldBeEmpty();
    }

    [Fact]
    public void ContributeVariables_NullJson_ReturnsEmpty()
    {
        var vars = _contributor.ContributeVariables(new EndpointContext { EndpointJson = null });

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

        var release = new ReleaseEntity { Version = "1.0.0" };

        IEndpointVariableContributor contributor = _contributor;

        var vars = await contributor.ContributeAdditionalVariablesAsync(snapshot, release, CancellationToken.None);

        vars.ShouldBeEmpty();
    }

}
