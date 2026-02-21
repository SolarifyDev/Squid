using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Squid.Agent.Configuration;
using Serilog;

namespace Squid.Agent.Registration;

public class AgentRegistrationClient
{
    private readonly AgentSettings _settings;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public AgentRegistrationClient(AgentSettings settings)
    {
        _settings = settings;
    }

    public async Task<RegistrationResult> RegisterAsync(
        string subscriptionId, string thumbprint, CancellationToken ct)
    {
        var maxRetries = 10;
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(60);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await SendRegistrationAsync(subscriptionId, thumbprint, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode.HasValue && (int)ex.StatusCode.Value >= 400 && (int)ex.StatusCode.Value < 500)
            {
                Log.Error(ex, "Registration failed with client error {StatusCode}, not retrying", ex.StatusCode);
                throw;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                Log.Warning(ex, "Registration attempt {Attempt}/{MaxRetries} failed, retrying in {Delay}s",
                    attempt, maxRetries, delay.TotalSeconds);

                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
            }
        }

        throw new InvalidOperationException("Registration failed after maximum retries");
    }

    private async Task<RegistrationResult> SendRegistrationAsync(
        string subscriptionId, string thumbprint, CancellationToken ct)
    {
        using var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri(_settings.ServerUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BearerToken);

        var machineName = string.IsNullOrEmpty(_settings.MachineName)
            ? $"k8s-agent-{subscriptionId[..8]}"
            : _settings.MachineName;

        var payload = new
        {
            machineName,
            thumbprint,
            subscriptionId,
            spaceId = _settings.SpaceId,
            roles = _settings.Roles,
            environmentIds = _settings.EnvironmentIds,
            @namespace = _settings.Namespace
        };

        var response = await client.PostAsJsonAsync("/api/agents/register", payload, JsonOptions, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<RegistrationResponse>(json, JsonOptions);

        Log.Information("Registration successful. MachineId={MachineId}, ServerThumbprint={ServerThumbprint}",
            result?.Data?.MachineId, result?.Data?.ServerThumbprint);

        return new RegistrationResult
        {
            MachineId = result?.Data?.MachineId ?? 0,
            ServerThumbprint = result?.Data?.ServerThumbprint ?? string.Empty,
            SubscriptionUri = result?.Data?.SubscriptionUri ?? $"poll://{subscriptionId}/"
        };
    }

    private class RegistrationResponse
    {
        public RegistrationResponseData Data { get; set; }
    }

    private class RegistrationResponseData
    {
        public int MachineId { get; set; }
        public string ServerThumbprint { get; set; }
        public string SubscriptionUri { get; set; }
    }
}

public class RegistrationResult
{
    public int MachineId { get; set; }
    public string ServerThumbprint { get; set; } = string.Empty;
    public string SubscriptionUri { get; set; } = string.Empty;
}
