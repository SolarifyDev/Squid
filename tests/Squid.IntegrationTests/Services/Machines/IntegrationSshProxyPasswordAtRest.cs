using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Security;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.IntegrationTests.Services.Machines;

/// <summary>
/// End-to-end (real Postgres + real DI + real AES-256-GCM) coverage of the SSH proxy-password at-rest
/// READ seam: a proxy password encrypted via the real <see cref="IAtRestSecretProtector"/> and persisted
/// inside a machine's endpoint JSON (Postgres jsonb) is transparently decrypted by the DI-resolved
/// <see cref="SshEndpointVariableContributor"/> — proving the protector is actually injected into the
/// contributor (the read seam is not a silent no-op). A legacy plaintext endpoint reads back verbatim
/// (read-both / non-breaking).
/// </summary>
public class IntegrationSshProxyPasswordAtRest : TestBase
{
    public IntegrationSshProxyPasswordAtRest()
        : base("SshProxyPasswordAtRest", "squid_it_ssh_proxy_atrest")
    {
    }

    [Fact]
    public async Task EncryptedProxyPassword_IsStoredAsEnvelope_AndDiContributorDecryptsIt()
    {
        const string secret = "proxy-secret-9c3f-VALUE";

        var envelope = await Run<IAtRestSecretProtector, string>(p =>
            Task.FromResult(p.Protect(secret, SshEndpointDto.ProxyPasswordKdfScope))).ConfigureAwait(false);

        envelope.ShouldStartWith("SQUID_ENCRYPTED_V2:", customMessage: "the real protector must emit a V2 envelope.");
        envelope.ShouldNotContain(secret);

        var machineId = await SeedSshMachineAsync(envelope).ConfigureAwait(false);

        await Run<IMachineDataProvider, IEnumerable<IEndpointVariableContributor>>(async (machines, contributors) =>
        {
            var machine = await machines.GetMachinesByIdAsync(machineId, CancellationToken.None).ConfigureAwait(false);

            // The raw column carries the envelope, never the cleartext.
            machine.Endpoint.ShouldContain("SQUID_ENCRYPTED_V2:");
            machine.Endpoint.ShouldNotContain(secret);

            var ssh = contributors.OfType<SshEndpointVariableContributor>().Single();
            var vars = ssh.ContributeVariables(new EndpointContext { EndpointJson = machine.Endpoint });

            vars.First(v => v.Name == SpecialVariables.Ssh.ProxyPassword).Value.ShouldBe(secret,
                customMessage: "the DI-resolved SSH contributor must decrypt the at-rest proxy password — proves the protector is wired into the read seam.");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task LegacyPlaintextProxyPassword_ReadsBackVerbatim()
    {
        // A machine registered before encryption has a plaintext proxy password in its endpoint JSON.
        // Read-both: the DI contributor returns it unchanged — non-breaking for existing machines.
        const string legacy = "legacy-plaintext-proxy";

        var machineId = await SeedSshMachineAsync(legacy).ConfigureAwait(false);

        await Run<IMachineDataProvider, IEnumerable<IEndpointVariableContributor>>(async (machines, contributors) =>
        {
            var machine = await machines.GetMachinesByIdAsync(machineId, CancellationToken.None).ConfigureAwait(false);

            var ssh = contributors.OfType<SshEndpointVariableContributor>().Single();
            var vars = ssh.ContributeVariables(new EndpointContext { EndpointJson = machine.Endpoint });

            vars.First(v => v.Name == SpecialVariables.Ssh.ProxyPassword).Value.ShouldBe(legacy);
        }).ConfigureAwait(false);
    }

    private async Task<int> SeedSshMachineAsync(string proxyPassword)
    {
        var endpointJson = JsonSerializer.Serialize(new SshEndpointDto
        {
            CommunicationStyle = "Ssh", Host = "ssh-host", Port = 22, ProxyPassword = proxyPassword
        });

        var machineId = 0;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var machine = new Machine
            {
                Name = $"ssh-{Guid.NewGuid():N}",
                IsDisabled = false,
                Roles = "[]",
                EnvironmentIds = "[]",
                SpaceId = 1,
                Endpoint = endpointJson,
                Slug = $"ssh-{Guid.NewGuid():N}"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
            machineId = machine.Id;
        }).ConfigureAwait(false);

        return machineId;
    }
}
