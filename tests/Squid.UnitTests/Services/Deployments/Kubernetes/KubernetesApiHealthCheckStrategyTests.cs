using System;
using System.Net.Http.Headers;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Http;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesApiHealthCheckStrategyTests
{
    private readonly Mock<IDeploymentAccountDataProvider> _accountDataProvider = new();
    private readonly Mock<ISquidHttpClientFactory> _httpClientFactory = new();
    private readonly KubernetesApiHealthCheckStrategy _strategy;

    public KubernetesApiHealthCheckStrategyTests()
    {
        _strategy = new KubernetesApiHealthCheckStrategy(_accountDataProvider.Object, _httpClientFactory.Object);
    }

    // ========================================================================
    // DefaultHealthCheckScript
    // ========================================================================

    [Fact]
    public void DefaultHealthCheckScript_ContainsKubectlClusterInfo()
    {
        _strategy.DefaultHealthCheckScript.ShouldContain("kubectl cluster-info");
    }

    // ========================================================================
    // ConnectivityTimeout
    // ========================================================================

    [Fact]
    public void DefaultConnectTimeoutSeconds_Is15()
    {
        KubernetesApiHealthCheckStrategy.DefaultConnectTimeoutSeconds.ShouldBe(15);
    }

    // ========================================================================
    // CheckConnectivityAsync — endpoint parsing
    // ========================================================================

    [Fact]
    public async Task CheckConnectivity_EmptyClusterUrl_ReturnsUnhealthy()
    {
        var machine = new Machine
        {
            Id = 1, Name = "no-cluster-url",
            Endpoint = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesApi" })
        };

        var result = await _strategy.CheckConnectivityAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("ClusterUrl is empty");
    }

    [Fact]
    public async Task CheckConnectivity_InvalidEndpointJson_ReturnsUnhealthy()
    {
        var machine = new Machine { Id = 1, Name = "bad-json", Endpoint = "not-json" };

        var result = await _strategy.CheckConnectivityAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("Failed to parse endpoint JSON");
    }

    [Fact]
    public async Task CheckConnectivity_WithTokenAuth_LoadsAccount()
    {
        var account = new DeploymentAccount
        {
            Id = 10, AccountType = AccountType.Token,
            Credentials = JsonSerializer.Serialize(new TokenCredentials { Token = "test-token-123" })
        };

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(account);

        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://unreachable-cluster.local:6443",
            SkipTlsVerification = "True",
            ResourceReferences = new[] { new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = 10 } }
        });

        var machine = new Machine { Id = 1, Name = "api-with-token", Endpoint = endpointJson };

        var result = await _strategy.CheckConnectivityAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        _accountDataProvider.Verify(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckConnectivity_NoAccount_StillAttempts()
    {
        var endpointJson = JsonSerializer.Serialize(new
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://unreachable-cluster.local:6443",
            SkipTlsVerification = "True"
        });

        var machine = new Machine { Id = 1, Name = "api-no-account", Endpoint = endpointJson };

        var result = await _strategy.CheckConnectivityAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldNotBeNullOrWhiteSpace();
    }

    // ========================================================================
    // ProbeClusterHealthAsync — HTTP behavior
    // ========================================================================

    [Fact]
    public async Task ProbeClusterHealth_InvalidUrl_ReturnsUnhealthy()
    {
        var result = await _strategy.ProbeClusterHealthAsync(
            "http://invalid-host-that-does-not-exist.local:9999", null, true, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ProbeClusterHealth_Cancelled_ReturnsFalse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _strategy.ProbeClusterHealthAsync(
            "https://localhost:6443", null, true, TimeSpan.FromSeconds(5), cts.Token);

        result.Healthy.ShouldBeFalse();
    }
}
