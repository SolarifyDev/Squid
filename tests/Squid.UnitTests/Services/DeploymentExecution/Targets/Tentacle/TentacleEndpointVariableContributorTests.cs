using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class TentacleEndpointVariableContributorTests
{
    private readonly TentacleEndpointVariableContributor _contributor = new();

    [Fact]
    public void ContributeVariables_LinuxListening_ContributesStyleAndUri()
    {
        var context = new EndpointContext
        {
            EndpointJson = """{"CommunicationStyle":"LinuxListening","Uri":"https://10.0.0.5:10933/","Thumbprint":"AABB"}"""
        };

        var vars = _contributor.ContributeVariables(context);

        vars.ShouldNotBeEmpty();
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.CommunicationStyle" && v.Value == "LinuxListening");
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.Thumbprint" && v.Value == "AABB");
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.Uri" && v.Value == "https://10.0.0.5:10933/");
        vars.ShouldNotContain(v => v.Name == "Squid.Tentacle.SubscriptionId");
    }

    [Fact]
    public void ContributeVariables_LinuxPolling_ContributesStyleAndSubscriptionId()
    {
        var context = new EndpointContext
        {
            EndpointJson = """{"CommunicationStyle":"LinuxPolling","SubscriptionId":"tentacle-01","Thumbprint":"CCDD"}"""
        };

        var vars = _contributor.ContributeVariables(context);

        vars.ShouldNotBeEmpty();
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.CommunicationStyle" && v.Value == "LinuxPolling");
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.Thumbprint" && v.Value == "CCDD");
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.SubscriptionId" && v.Value == "tentacle-01");
        vars.ShouldNotContain(v => v.Name == "Squid.Tentacle.Uri");
    }

    [Fact]
    public void ContributeVariables_MissingCommunicationStyle_ReturnsEmpty()
    {
        var context = new EndpointContext { EndpointJson = """{"Thumbprint":"AABB"}""" };

        var vars = _contributor.ContributeVariables(context);

        vars.ShouldBeEmpty();
    }

    [Fact]
    public void ContributeVariables_EmptyEndpointJson_ReturnsEmpty()
    {
        var context = new EndpointContext { EndpointJson = "" };

        var vars = _contributor.ContributeVariables(context);

        vars.ShouldBeEmpty();
    }

    [Fact]
    public void ParseResourceReferences_AlwaysReturnsEmpty()
    {
        var refs = _contributor.ParseResourceReferences("""{"CommunicationStyle":"LinuxListening"}""");

        refs.ShouldNotBeNull();
        refs.References.ShouldBeEmpty();
    }
}
