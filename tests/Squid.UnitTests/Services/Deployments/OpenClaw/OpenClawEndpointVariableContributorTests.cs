using System.Linq;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.OpenClaw;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;

using static Squid.Message.Enums.EndpointResourceType;

namespace Squid.UnitTests.Services.Deployments.OpenClaw;

public class OpenClawEndpointVariableContributorTests
{
    private readonly OpenClawEndpointVariableContributor _contributor = new();

    private static string MakeEndpointJson(
        string baseUrl = "https://claw.example.com:18789",
        string inlineGatewayToken = null,
        string inlineHooksToken = null,
        List<EndpointResourceReference> resourceReferences = null) =>
        JsonSerializer.Serialize(new OpenClawEndpointDto
        {
            CommunicationStyle = "OpenClaw",
            BaseUrl = baseUrl,
            InlineGatewayToken = inlineGatewayToken,
            InlineHooksToken = inlineHooksToken,
            ResourceReferences = resourceReferences ?? new List<EndpointResourceReference>
            {
                new() { Type = AuthenticationAccount, ResourceId = 1 }
            }
        });

    private static EndpointContext AccountContext()
    {
        var ctx = new EndpointContext { EndpointJson = MakeEndpointJson() };
        ctx.SetAccountData(AccountType.OpenClawGateway, JsonSerializer.Serialize(new OpenClawGatewayCredentials
        {
            GatewayToken = "gw-token-abc",
            HooksToken = "hooks-token-xyz"
        }));
        return ctx;
    }

    private static EndpointContext InlineContext()
    {
        var json = MakeEndpointJson(
            inlineGatewayToken: "inline-gw",
            inlineHooksToken: "inline-hooks",
            resourceReferences: new());

        return new EndpointContext { EndpointJson = json };
    }

    // ========================================================================
    // ParseResourceReferences
    // ========================================================================

    [Fact]
    public void ParseResourceReferences_ValidEndpoint_ReturnsReferences()
    {
        var refs = _contributor.ParseResourceReferences(MakeEndpointJson());

        refs.References.ShouldNotBeEmpty();
        refs.FindFirst(AuthenticationAccount).ShouldBe(1);
    }

    [Fact]
    public void ParseResourceReferences_InvalidJson_ReturnsEmpty()
    {
        var refs = _contributor.ParseResourceReferences("not-json");

        refs.References.ShouldBeEmpty();
    }

    [Fact]
    public void ParseResourceReferences_NullJson_ReturnsEmpty()
    {
        var refs = _contributor.ParseResourceReferences(null);

        refs.References.ShouldBeEmpty();
    }

    // ========================================================================
    // ContributeVariables — Account-based tokens
    // ========================================================================

    [Fact]
    public void ContributeVariables_WithAccount_ContributesBaseUrl()
    {
        var vars = _contributor.ContributeVariables(AccountContext());

        vars.ShouldContain(v => v.Name == SpecialVariables.OpenClaw.BaseUrl && v.Value == "https://claw.example.com:18789");
    }

    [Fact]
    public void ContributeVariables_WithAccount_ContributesGatewayToken()
    {
        var vars = _contributor.ContributeVariables(AccountContext());

        var token = vars.First(v => v.Name == SpecialVariables.OpenClaw.GatewayToken);
        token.Value.ShouldBe("gw-token-abc");
        token.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void ContributeVariables_WithAccount_ContributesHooksToken()
    {
        var vars = _contributor.ContributeVariables(AccountContext());

        var token = vars.First(v => v.Name == SpecialVariables.OpenClaw.HooksToken);
        token.Value.ShouldBe("hooks-token-xyz");
        token.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void ContributeVariables_WithAccount_DoesNotContainSessionKey()
    {
        var vars = _contributor.ContributeVariables(AccountContext());

        vars.ShouldNotContain(v => v.Name == "Squid.Action.OpenClaw.SessionKey");
    }

    // ========================================================================
    // ContributeVariables — Inline tokens (no account)
    // ========================================================================

    [Fact]
    public void ContributeVariables_InlineTokens_FallsBackToEndpointValues()
    {
        var vars = _contributor.ContributeVariables(InlineContext());

        vars.First(v => v.Name == SpecialVariables.OpenClaw.GatewayToken).Value.ShouldBe("inline-gw");
        vars.First(v => v.Name == SpecialVariables.OpenClaw.HooksToken).Value.ShouldBe("inline-hooks");
    }

    [Fact]
    public void ContributeVariables_InlineTokens_NoAccountVariables()
    {
        var vars = _contributor.ContributeVariables(InlineContext());

        vars.ShouldNotContain(v => v.Name == SpecialVariables.Account.AccountType);
    }

    // ========================================================================
    // ContributeVariables — Edge cases
    // ========================================================================

    [Fact]
    public void ContributeVariables_InvalidJson_ReturnsEmpty()
    {
        var ctx = new EndpointContext { EndpointJson = "garbage" };

        _contributor.ContributeVariables(ctx).ShouldBeEmpty();
    }
}
