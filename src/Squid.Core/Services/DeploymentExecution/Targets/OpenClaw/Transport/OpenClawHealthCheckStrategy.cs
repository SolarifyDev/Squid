using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Http;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;

namespace Squid.Core.Services.DeploymentExecution.OpenClaw;

public class OpenClawHealthCheckStrategy : IHealthCheckStrategy
{
    internal const int DefaultConnectTimeoutSeconds = 15;

    private readonly IDeploymentAccountDataProvider _accountDataProvider;
    private readonly ISquidHttpClientFactory _httpClientFactory;

    public OpenClawHealthCheckStrategy(IDeploymentAccountDataProvider accountDataProvider, ISquidHttpClientFactory httpClientFactory)
    {
        _accountDataProvider = accountDataProvider;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(Machine machine, MachineConnectivityPolicyDto connectivityPolicy, CancellationToken ct, MachineHealthCheckPolicyDto healthCheckPolicy = null)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<OpenClawEndpointDto>(machine.Endpoint);

        if (endpoint == null)
            return new HealthCheckResult(false, "Failed to parse OpenClaw endpoint JSON");

        if (string.IsNullOrEmpty(endpoint.BaseUrl))
            return new HealthCheckResult(false, "BaseUrl is empty");

        var gatewayToken = await ResolveGatewayTokenAsync(endpoint, ct).ConfigureAwait(false);
        var timeout = TimeSpan.FromSeconds(connectivityPolicy?.ConnectTimeoutSeconds ?? DefaultConnectTimeoutSeconds);

        return await ProbeGatewayAsync(endpoint.BaseUrl, gatewayToken, timeout, ct).ConfigureAwait(false);
    }

    internal async Task<HealthCheckResult> ProbeGatewayAsync(string baseUrl, string gatewayToken, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var url = $"{baseUrl.TrimEnd('/')}/tools/invoke";
            var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {gatewayToken}" };

            var client = _httpClientFactory.CreateClient(timeout: timeout, headers: headers);
            var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if ((int)response.StatusCode == 401)
                return new HealthCheckResult(false, "OpenClaw returned 401 Unauthorized — gateway token may be invalid");

            return new HealthCheckResult(true, $"OpenClaw gateway reachable (HTTP {(int)response.StatusCode})");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthCheckResult(false, "OpenClaw gateway connection timed out");
        }
        catch (HttpRequestException ex)
        {
            return new HealthCheckResult(false, $"OpenClaw gateway connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(false, $"OpenClaw gateway check error: {ex.Message}");
        }
    }

    private async Task<string> ResolveGatewayTokenAsync(OpenClawEndpointDto endpoint, CancellationToken ct)
    {
        var refs = new EndpointResourceReferences { References = endpoint.ResourceReferences ?? new() };
        var accountId = refs.FindFirst(EndpointResourceType.AuthenticationAccount);

        if (accountId == null)
            return endpoint.InlineGatewayToken ?? string.Empty;

        var account = await _accountDataProvider.GetAccountByIdAsync(accountId.Value, ct).ConfigureAwait(false);

        if (account == null)
            return endpoint.InlineGatewayToken ?? string.Empty;

        var credentials = DeploymentAccountCredentialsConverter.Deserialize(account.AccountType, account.Credentials);

        if (credentials is OpenClawGatewayCredentials oc && !string.IsNullOrEmpty(oc.GatewayToken))
            return oc.GatewayToken;

        if (credentials is TokenCredentials tc && !string.IsNullOrEmpty(tc.Token))
            return tc.Token;

        return endpoint.InlineGatewayToken ?? string.Empty;
    }
}
