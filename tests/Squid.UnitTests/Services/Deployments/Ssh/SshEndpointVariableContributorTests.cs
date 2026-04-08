using System.Linq;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;

using static Squid.Message.Enums.EndpointResourceType;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshEndpointVariableContributorTests
{
    private readonly SshEndpointVariableContributor _contributor = new();

    private static string MakeEndpointJson(
        string host = "ssh.example.com",
        int port = 22,
        string fingerprint = "abc123",
        string remoteWorkDir = null,
        List<EndpointResourceReference> resourceReferences = null) =>
        JsonSerializer.Serialize(new SshEndpointDto
        {
            CommunicationStyle = "Ssh",
            Host = host,
            Port = port,
            Fingerprint = fingerprint,
            RemoteWorkingDirectory = remoteWorkDir,
            ResourceReferences = resourceReferences ?? new List<EndpointResourceReference>
            {
                new() { Type = AuthenticationAccount, ResourceId = 1 }
            }
        });

    private static EndpointContext SshKeyPairContext()
    {
        var ctx = new EndpointContext { EndpointJson = MakeEndpointJson() };
        ctx.SetAccountData(AccountType.SshKeyPair, JsonSerializer.Serialize(new SshKeyPairCredentials
        {
            Username = "deploy",
            PrivateKeyFile = "-----BEGIN RSA PRIVATE KEY-----\nMIIE...\n-----END RSA PRIVATE KEY-----",
            PrivateKeyPassphrase = "secret123"
        }));
        return ctx;
    }

    private static EndpointContext UsernamePasswordContext()
    {
        var ctx = new EndpointContext { EndpointJson = MakeEndpointJson() };
        ctx.SetAccountData(AccountType.UsernamePassword, JsonSerializer.Serialize(new UsernamePasswordCredentials
        {
            Username = "admin",
            Password = "pass123"
        }));
        return ctx;
    }

    private static EndpointContext NoAccountContext()
    {
        var json = MakeEndpointJson(resourceReferences: new());
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
    // ContributeVariables — SshKeyPair
    // ========================================================================

    [Fact]
    public void ContributeVariables_SshKeyPair_ContributesHost()
    {
        var vars = _contributor.ContributeVariables(SshKeyPairContext());

        vars.ShouldContain(v => v.Name == SpecialVariables.Ssh.Host && v.Value == "ssh.example.com");
    }

    [Fact]
    public void ContributeVariables_SshKeyPair_ContributesPort()
    {
        var vars = _contributor.ContributeVariables(SshKeyPairContext());

        vars.ShouldContain(v => v.Name == SpecialVariables.Ssh.Port && v.Value == "22");
    }

    [Fact]
    public void ContributeVariables_SshKeyPair_ContributesFingerprint()
    {
        var vars = _contributor.ContributeVariables(SshKeyPairContext());

        vars.ShouldContain(v => v.Name == SpecialVariables.Ssh.Fingerprint && v.Value == "abc123");
    }

    [Fact]
    public void ContributeVariables_SshKeyPair_ContributesUsername()
    {
        var vars = _contributor.ContributeVariables(SshKeyPairContext());

        vars.ShouldContain(v => v.Name == SpecialVariables.Account.Username && v.Value == "deploy");
    }

    [Fact]
    public void ContributeVariables_SshKeyPair_ContributesPrivateKey()
    {
        var vars = _contributor.ContributeVariables(SshKeyPairContext());

        var key = vars.First(v => v.Name == SpecialVariables.Account.SshPrivateKeyFile);
        key.Value.ShouldContain("BEGIN RSA PRIVATE KEY");
        key.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void ContributeVariables_SshKeyPair_ContributesPassphrase()
    {
        var vars = _contributor.ContributeVariables(SshKeyPairContext());

        var passphrase = vars.First(v => v.Name == SpecialVariables.Account.SshPassphrase);
        passphrase.Value.ShouldBe("secret123");
        passphrase.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void ContributeVariables_SshKeyPair_ContributesAccountType()
    {
        var vars = _contributor.ContributeVariables(SshKeyPairContext());

        vars.ShouldContain(v => v.Name == SpecialVariables.Account.AccountType && v.Value == "SshKeyPair");
    }

    // ========================================================================
    // ContributeVariables — UsernamePassword
    // ========================================================================

    [Fact]
    public void ContributeVariables_UsernamePassword_ContributesUsername()
    {
        var vars = _contributor.ContributeVariables(UsernamePasswordContext());

        vars.ShouldContain(v => v.Name == SpecialVariables.Account.Username && v.Value == "admin");
    }

    [Fact]
    public void ContributeVariables_UsernamePassword_ContributesPassword()
    {
        var vars = _contributor.ContributeVariables(UsernamePasswordContext());

        var password = vars.First(v => v.Name == SpecialVariables.Account.Password);
        password.Value.ShouldBe("pass123");
        password.IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public void ContributeVariables_UsernamePassword_ContributesAccountType()
    {
        var vars = _contributor.ContributeVariables(UsernamePasswordContext());

        vars.ShouldContain(v => v.Name == SpecialVariables.Account.AccountType && v.Value == "UsernamePassword");
    }

    // ========================================================================
    // ContributeVariables — RemoteWorkingDirectory
    // ========================================================================

    [Fact]
    public void ContributeVariables_ContributesRemoteWorkingDirectory()
    {
        var json = MakeEndpointJson(remoteWorkDir: "/opt/deploy");
        var ctx = new EndpointContext { EndpointJson = json };
        ctx.SetAccountData(AccountType.SshKeyPair, JsonSerializer.Serialize(new SshKeyPairCredentials
        {
            Username = "deploy", PrivateKeyFile = "key", PrivateKeyPassphrase = ""
        }));

        var vars = _contributor.ContributeVariables(ctx);

        vars.ShouldContain(v => v.Name == SpecialVariables.Ssh.RemoteWorkingDirectory && v.Value == "/opt/deploy");
    }

    [Fact]
    public void ContributeVariables_NullRemoteWorkDir_ContributesEmpty()
    {
        var vars = _contributor.ContributeVariables(NoAccountContext());

        vars.ShouldContain(v => v.Name == SpecialVariables.Ssh.RemoteWorkingDirectory && v.Value == "");
    }

    // ========================================================================
    // ContributeVariables — Variable Counts
    // ========================================================================

    [Fact]
    public void ContributeVariables_SshKeyPair_TotalCount()
    {
        // 10 endpoint (Machine.Hostname, Host, Port, Fingerprint, RemoteWorkingDirectory, ProxyType, ProxyHost, ProxyPort, ProxyUsername, ProxyPassword) + 2 account meta (AccountType, CredentialsJson) + 3 SSH creds (Username, PrivateKey, Passphrase) = 15
        var vars = _contributor.ContributeVariables(SshKeyPairContext());

        vars.Count.ShouldBe(15);
    }

    [Fact]
    public void ContributeVariables_UsernamePassword_TotalCount()
    {
        // 10 endpoint + 2 account meta + 2 creds (Username, Password) = 14
        var vars = _contributor.ContributeVariables(UsernamePasswordContext());

        vars.Count.ShouldBe(14);
    }

    [Fact]
    public void ContributeVariables_NoAccount_TotalCount()
    {
        // 10 endpoint only
        var vars = _contributor.ContributeVariables(NoAccountContext());

        vars.Count.ShouldBe(10);
    }

    // ========================================================================
    // ContributeVariables — No Account
    // ========================================================================

    [Fact]
    public void ContributeVariables_NoAccount_ContributesEndpointOnly()
    {
        var vars = _contributor.ContributeVariables(NoAccountContext());

        vars.ShouldContain(v => v.Name == SpecialVariables.Ssh.Host);
        vars.ShouldContain(v => v.Name == SpecialVariables.Ssh.Port);
        vars.ShouldContain(v => v.Name == SpecialVariables.Ssh.Fingerprint);
        vars.ShouldContain(v => v.Name == SpecialVariables.Ssh.RemoteWorkingDirectory);
        vars.ShouldNotContain(v => v.Name == SpecialVariables.Account.AccountType);
    }

    // ========================================================================
    // Edge Cases
    // ========================================================================

    [Fact]
    public void ContributeVariables_InvalidJson_ReturnsEmpty()
    {
        var ctx = new EndpointContext { EndpointJson = "garbage" };

        _contributor.ContributeVariables(ctx).ShouldBeEmpty();
    }
}
