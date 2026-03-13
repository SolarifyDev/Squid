using System.Net.Http.Headers;
using System.Text;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Http;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesApiHealthCheckStrategy : IHealthCheckStrategy
{
    internal static readonly TimeSpan ConnectivityTimeout = TimeSpan.FromSeconds(15);

    private readonly IDeploymentAccountDataProvider _accountDataProvider;
    private readonly ISquidHttpClientFactory _httpClientFactory;

    public KubernetesApiHealthCheckStrategy(IDeploymentAccountDataProvider accountDataProvider, ISquidHttpClientFactory httpClientFactory)
    {
        _accountDataProvider = accountDataProvider;
        _httpClientFactory = httpClientFactory;
    }

    public string DefaultHealthCheckScript => """
                                              #!/bin/bash
                                              echo "Health check started (KubernetesApi)"
                                              echo "Hostname: $(hostname)"
                                              echo "Date: $(date -u)"
                                              kubectl cluster-info 2>&1
                                              kubectl get nodes -o wide 2>&1
                                              echo "Health check completed"
                                              exit 0
                                              """;

    public async Task<HealthCheckResult> CheckConnectivityAsync(Machine machine, CancellationToken ct)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<KubernetesApiEndpointDto>(machine.Endpoint);

        if (endpoint == null)
            return new HealthCheckResult(false, "Failed to parse endpoint JSON");

        if (string.IsNullOrEmpty(endpoint.ClusterUrl))
            return new HealthCheckResult(false, "ClusterUrl is empty");

        var authHeader = await BuildAuthHeaderAsync(endpoint, ct).ConfigureAwait(false);
        var skipTls = string.Equals(endpoint.SkipTlsVerification, "True", StringComparison.OrdinalIgnoreCase);

        return await ProbeClusterHealthAsync(endpoint.ClusterUrl, authHeader, skipTls, ct).ConfigureAwait(false);
    }

    private async Task<AuthenticationHeaderValue> BuildAuthHeaderAsync(KubernetesApiEndpointDto endpoint, CancellationToken ct)
    {
        var refs = new EndpointResourceReferences { References = endpoint.ResourceReferences ?? new() };
        var accountId = refs.FindFirst(EndpointResourceType.AuthenticationAccount);

        if (accountId == null) return null;

        var account = await _accountDataProvider.GetAccountByIdAsync(accountId.Value, ct).ConfigureAwait(false);

        if (account == null) return null;

        var credentials = DeploymentAccountCredentialsConverter.Deserialize(account.AccountType, account.Credentials);

        return credentials switch
        {
            TokenCredentials token when !string.IsNullOrEmpty(token.Token) => new AuthenticationHeaderValue("Bearer", token.Token),
            UsernamePasswordCredentials basic => new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basic.Username}:{basic.Password}"))),
            _ => null
        };
    }

    internal async Task<HealthCheckResult> ProbeClusterHealthAsync(string clusterUrl, AuthenticationHeaderValue authHeader, bool skipTls, CancellationToken ct)
    {
        try
        {
            using var client = CreateProbeClient(skipTls, authHeader);

            var healthUrl = $"{clusterUrl.TrimEnd('/')}/healthz";
            var response = await client.GetAsync(healthUrl, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return new HealthCheckResult(true, $"Cluster API healthy ({(int)response.StatusCode}): {body.Trim()}");

            return new HealthCheckResult(false, $"Cluster API returned {(int)response.StatusCode}: {body.Trim()}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthCheckResult(false, "Cluster API connection timed out");
        }
        catch (HttpRequestException ex)
        {
            return new HealthCheckResult(false, $"Cluster API connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(false, $"Cluster API check error: {ex.Message}");
        }
    }

    private HttpClient CreateProbeClient(bool skipTls, AuthenticationHeaderValue authHeader)
    {
        if (skipTls)
        {
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            var client = new HttpClient(handler) { Timeout = ConnectivityTimeout };

            if (authHeader != null)
                client.DefaultRequestHeaders.Authorization = authHeader;

            return client;
        }

        var headers = authHeader != null
            ? new Dictionary<string, string> { ["Authorization"] = authHeader.ToString() }
            : null;

        return _httpClientFactory.CreateClient(timeout: ConnectivityTimeout, headers: headers);
    }
}
