using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments.Ssh;

/// <summary>
/// Unit coverage for the SSH proxy-password at-rest READ seam: the variable contributor must DECRYPT the
/// envelope stored in the endpoint JSON before contributing it as the SSH connection variable, must pass a
/// legacy plaintext through verbatim (read-both / non-breaking), and — when no protector is supplied (the
/// health check's internal use, or tests) — must leave the value untouched.
/// </summary>
public sealed class SshProxyPasswordAtRestTests
{
    private static IAtRestSecretProtector RealProtector()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Security:VariableEncryption:MasterKey"] = System.Convert.ToBase64String(key)
            })
            .Build();

        return new AtRestSecretProtector(new VariableEncryptionService(new SecuritySetting(config)));
    }

    private static string ContributedProxyPassword(SshEndpointVariableContributor contributor, string endpointProxyPassword)
    {
        var json = JsonSerializer.Serialize(new SshEndpointDto { Host = "h", Port = 22, ProxyPassword = endpointProxyPassword });

        var vars = contributor.ContributeVariables(new EndpointContext { EndpointJson = json });

        return vars.First(v => v.Name == SpecialVariables.Ssh.ProxyPassword).Value;
    }

    [Fact]
    public void Contributor_WithProtector_DecryptsEnvelope_BeforeContributing()
    {
        var protector = RealProtector();
        var envelope = protector.Protect("proxy-secret", SshEndpointDto.ProxyPasswordKdfScope);

        envelope.ShouldStartWith("SQUID_ENCRYPTED_V2:", customMessage: "test setup: the stored value must be an envelope.");

        ContributedProxyPassword(new SshEndpointVariableContributor(protector), envelope)
            .ShouldBe("proxy-secret",
                customMessage: "the contributor must decrypt the at-rest envelope so the SSH connection gets the real proxy password.");
    }

    [Fact]
    public void Contributor_WithProtector_LegacyPlaintext_PassesThrough_ReadBoth()
    {
        // An existing machine has a plaintext proxy password in its endpoint JSON. Read-both: it flows
        // through unchanged, so the feature is non-breaking for machines registered before encryption.
        ContributedProxyPassword(new SshEndpointVariableContributor(RealProtector()), "legacy-plaintext")
            .ShouldBe("legacy-plaintext");
    }

    [Fact]
    public void Contributor_WithoutProtector_LeavesValueUntouched()
    {
        // No protector (health check's internal contributor use, or tests) → no decrypt attempt.
        ContributedProxyPassword(new SshEndpointVariableContributor(), "whatever-value")
            .ShouldBe("whatever-value");
    }
}
