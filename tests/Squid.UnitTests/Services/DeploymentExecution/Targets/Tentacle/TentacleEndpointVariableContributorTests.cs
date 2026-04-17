using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class TentacleEndpointVariableContributorTests
{
    private readonly TentacleEndpointVariableContributor _contributor = new();

    [Fact]
    public void ContributeVariables_TentacleListening_ContributesStyleAndUri()
    {
        var context = new EndpointContext
        {
            EndpointJson = """{"CommunicationStyle":"TentacleListening","Uri":"https://10.0.0.5:10933/","Thumbprint":"AABB"}"""
        };

        var vars = _contributor.ContributeVariables(context);

        vars.ShouldNotBeEmpty();
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.CommunicationStyle" && v.Value == "TentacleListening");
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.Thumbprint" && v.Value == "AABB");
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.Uri" && v.Value == "https://10.0.0.5:10933/");
        vars.ShouldNotContain(v => v.Name == "Squid.Tentacle.SubscriptionId");
    }

    [Fact]
    public void ContributeVariables_TentaclePolling_ContributesStyleAndSubscriptionId()
    {
        var context = new EndpointContext
        {
            EndpointJson = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"tentacle-01","Thumbprint":"CCDD"}"""
        };

        var vars = _contributor.ContributeVariables(context);

        vars.ShouldNotBeEmpty();
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.CommunicationStyle" && v.Value == "TentaclePolling");
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
        var refs = _contributor.ParseResourceReferences("""{"CommunicationStyle":"TentacleListening"}""");

        refs.ShouldNotBeNull();
        refs.References.ShouldBeEmpty();
    }

    // ========== Phase 3: runtime capabilities enrichment ==========

    [Fact]
    public void ContributeVariables_WithCacheHit_AddsOsAndShellVariables()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(123, new Dictionary<string, string>
        {
            ["os"] = "Windows",
            ["defaultShell"] = "pwsh",
            ["installedShells"] = "pwsh,powershell",
            ["architecture"] = "X64"
        }, agentVersion: "3.1.0");

        var contributor = new TentacleEndpointVariableContributor(cache);
        var context = new EndpointContext
        {
            EndpointJson = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"AA"}""",
            MachineId = 123
        };

        var vars = contributor.ContributeVariables(context);

        vars.ShouldContain(v => v.Name == "Squid.Tentacle.OS" && v.Value == "Windows");
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.DefaultShell" && v.Value == "pwsh");
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.InstalledShells" && v.Value == "pwsh,powershell");
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.Architecture" && v.Value == "X64");
        vars.ShouldContain(v => v.Name == "Squid.Tentacle.AgentVersion" && v.Value == "3.1.0");
    }

    [Fact]
    public void ContributeVariables_CacheMiss_DoesNotContributeRuntimeVars()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        var contributor = new TentacleEndpointVariableContributor(cache);
        var context = new EndpointContext
        {
            EndpointJson = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"AA"}""",
            MachineId = 999   // not in cache
        };

        var vars = contributor.ContributeVariables(context);

        vars.ShouldNotContain(v => v.Name == "Squid.Tentacle.OS");
        vars.ShouldNotContain(v => v.Name == "Squid.Tentacle.DefaultShell");
    }

    [Fact]
    public void ContributeVariables_NullMachineId_SkipsRuntimeCaps()
    {
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        cache.Store(1, new Dictionary<string, string> { ["os"] = "Linux" }, "1.0");
        var contributor = new TentacleEndpointVariableContributor(cache);
        var context = new EndpointContext
        {
            EndpointJson = """{"CommunicationStyle":"TentaclePolling","SubscriptionId":"sub-1","Thumbprint":"AA"}"""
            // MachineId intentionally null
        };

        var vars = contributor.ContributeVariables(context);

        vars.ShouldNotContain(v => v.Name == "Squid.Tentacle.OS");
    }
}
