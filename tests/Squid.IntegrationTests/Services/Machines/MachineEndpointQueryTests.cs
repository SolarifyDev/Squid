using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines;
using Squid.Message.Enums;

namespace Squid.IntegrationTests.Services.Machines;

public class MachineEndpointQueryTests : TestBase
{
    public MachineEndpointQueryTests()
        : base("MachineEndpointQuery", "squid_it_machine_endpoint_query")
    {
    }

    #region GetMachineByEndpointUriAsync

    [Fact]
    public async Task GetMachineByEndpointUriAsync_WhenUriExists_ReturnsMachine()
    {
        var uri = $"https://agent-{Guid.NewGuid()}.example.com:10933";
        await SeedMachineAsync(uri: uri);

        await Run<IMachineDataProvider>(async provider =>
        {
            var machine = await provider.GetMachineByEndpointUriAsync(uri, CancellationToken.None)
                .ConfigureAwait(false);

            machine.ShouldNotBeNull();
            EndpointJsonHelper.GetField(machine.Endpoint, "Uri").ShouldBe(uri);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetMachineByEndpointUriAsync_WhenUriDoesNotExist_ReturnsNull()
    {
        await SeedMachineAsync(uri: "https://existing.example.com:10933");

        await Run<IMachineDataProvider>(async provider =>
        {
            var machine = await provider.GetMachineByEndpointUriAsync(
                "https://nonexistent.example.com:10933", CancellationToken.None).ConfigureAwait(false);

            machine.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetMachineByEndpointUriAsync_WithMultipleMachines_ReturnsCorrectOne()
    {
        var targetUri = $"https://target-{Guid.NewGuid()}.example.com:10933";
        var otherUri = $"https://other-{Guid.NewGuid()}.example.com:10933";
        await SeedMachineAsync(name: "Target Agent", uri: targetUri);
        await SeedMachineAsync(name: "Other Agent", uri: otherUri);

        await Run<IMachineDataProvider>(async provider =>
        {
            var machine = await provider.GetMachineByEndpointUriAsync(targetUri, CancellationToken.None)
                .ConfigureAwait(false);

            machine.ShouldNotBeNull();
            machine.Name.ShouldBe("Target Agent");
        }).ConfigureAwait(false);
    }

    #endregion

    #region GetMachineBySubscriptionIdAsync

    [Fact]
    public async Task GetMachineBySubscriptionIdAsync_WhenExists_ReturnsMachine()
    {
        var subscriptionId = Guid.NewGuid().ToString();
        await SeedMachineAsync(subscriptionId: subscriptionId);

        await Run<IMachineDataProvider>(async provider =>
        {
            var machine = await provider.GetMachineBySubscriptionIdAsync(subscriptionId, CancellationToken.None)
                .ConfigureAwait(false);

            machine.ShouldNotBeNull();
            EndpointJsonHelper.GetField(machine.Endpoint, "SubscriptionId").ShouldBe(subscriptionId);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetMachineBySubscriptionIdAsync_WhenNotExists_ReturnsNull()
    {
        await SeedMachineAsync(subscriptionId: Guid.NewGuid().ToString());

        await Run<IMachineDataProvider>(async provider =>
        {
            var machine = await provider.GetMachineBySubscriptionIdAsync(
                "nonexistent-sub-id", CancellationToken.None).ConfigureAwait(false);

            machine.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    #endregion

    #region ExistsBySubscriptionIdAsync

    [Fact]
    public async Task ExistsBySubscriptionIdAsync_WhenExists_ReturnsTrue()
    {
        var subscriptionId = Guid.NewGuid().ToString();
        await SeedMachineAsync(subscriptionId: subscriptionId);

        await Run<IMachineDataProvider>(async provider =>
        {
            var exists = await provider.ExistsBySubscriptionIdAsync(subscriptionId, CancellationToken.None)
                .ConfigureAwait(false);

            exists.ShouldBeTrue();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task ExistsBySubscriptionIdAsync_WhenNotExists_ReturnsFalse()
    {
        await Run<IMachineDataProvider>(async provider =>
        {
            var exists = await provider.ExistsBySubscriptionIdAsync(
                "nonexistent-sub-id", CancellationToken.None).ConfigureAwait(false);

            exists.ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    #endregion

    #region GetPollingThumbprintsAsync

    [Fact]
    public async Task GetPollingThumbprintsAsync_ReturnsThumbprintsForPollingMachines()
    {
        var thumbprint = $"THUMB-{Guid.NewGuid():N}";
        await SeedMachineAsync(
            name: "Polling Agent",
            subscriptionId: Guid.NewGuid().ToString(),
            thumbprint: thumbprint);

        await Run<IMachineDataProvider>(async provider =>
        {
            var thumbprints = await provider.GetPollingThumbprintsAsync(CancellationToken.None)
                .ConfigureAwait(false);

            thumbprints.ShouldContain(thumbprint);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetPollingThumbprintsAsync_ExcludesNonPollingMachines()
    {
        // Listening machine (has URI but no SubscriptionId)
        await SeedMachineAsync(
            name: "Listening Agent",
            uri: $"https://listener-{Guid.NewGuid()}.example.com:10933",
            thumbprint: "LISTENER-THUMB");

        await Run<IMachineDataProvider>(async provider =>
        {
            var thumbprints = await provider.GetPollingThumbprintsAsync(CancellationToken.None)
                .ConfigureAwait(false);

            thumbprints.ShouldNotContain("LISTENER-THUMB");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetPollingThumbprintsAsync_ReturnsDistinctThumbprints()
    {
        var sharedThumbprint = $"SHARED-{Guid.NewGuid():N}";
        await SeedMachineAsync(
            name: "Polling Agent 1",
            subscriptionId: Guid.NewGuid().ToString(),
            thumbprint: sharedThumbprint);
        await SeedMachineAsync(
            name: "Polling Agent 2",
            subscriptionId: Guid.NewGuid().ToString(),
            thumbprint: sharedThumbprint);

        await Run<IMachineDataProvider>(async provider =>
        {
            var thumbprints = await provider.GetPollingThumbprintsAsync(CancellationToken.None)
                .ConfigureAwait(false);

            thumbprints.Count(t => t == sharedThumbprint).ShouldBe(1);
        }).ConfigureAwait(false);
    }

    #endregion

    #region Helpers

    private async Task<int> SeedMachineAsync(
        string name = null,
        string uri = null,
        string subscriptionId = null,
        string thumbprint = null)
    {
        var machineId = 0;
        name ??= $"Test Agent {Guid.NewGuid():N}";

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var machine = new Machine
            {
                Name = name,
                IsDisabled = false,
                Roles = JsonSerializer.Serialize(new[] { "web-server" }),
                EnvironmentIds = JsonSerializer.Serialize(new[] { 1 }),
                SpaceId = 1,
                Endpoint = BuildEndpointJson(uri, subscriptionId, thumbprint),
                Slug = $"test-agent-{Guid.NewGuid():N}"
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
            machineId = machine.Id;
        }).ConfigureAwait(false);

        return machineId;
    }

    private static string BuildEndpointJson(
        string uri = null,
        string subscriptionId = null,
        string thumbprint = null)
    {
        var endpoint = new Dictionary<string, object>();

        if (uri != null)
        {
            endpoint["CommunicationStyle"] = "TentaclePassive";
            endpoint["Uri"] = uri;
        }
        else if (subscriptionId != null)
        {
            endpoint["CommunicationStyle"] = "KubernetesAgent";
        }

        if (subscriptionId != null)
            endpoint["SubscriptionId"] = subscriptionId;

        if (thumbprint != null)
            endpoint["Thumbprint"] = thumbprint;

        return JsonSerializer.Serialize(endpoint);
    }

    #endregion
}
