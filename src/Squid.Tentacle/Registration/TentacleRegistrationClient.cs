using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Squid.Tentacle.Configuration;
using Serilog;

namespace Squid.Tentacle.Registration;

public class TentacleRegistrationClient
{
    private readonly TentacleSettings _settings;
    private readonly string _registrationPath;
    private readonly Dictionary<string, string> _extraProperties;
    private readonly TentacleRegistrationClientOptions _options;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public TentacleRegistrationClient(
        TentacleSettings settings,
        string registrationPath,
        Dictionary<string, string> extraProperties = null)
        : this(settings, registrationPath, extraProperties, options: null)
    {
    }

    public TentacleRegistrationClient(
        TentacleSettings settings,
        string registrationPath,
        Dictionary<string, string> extraProperties,
        TentacleRegistrationClientOptions options = null)
    {
        _settings = settings;
        _registrationPath = registrationPath;
        _extraProperties = extraProperties ?? new Dictionary<string, string>();
        _options = options ?? TentacleRegistrationClientOptions.Default;
    }

    public async Task<RegistrationResult> RegisterAsync(
        string subscriptionId, string thumbprint, CancellationToken ct)
    {
        var maxRetries = _options.MaxRetries;
        var delay = _options.InitialDelay;
        var maxDelay = _options.MaxDelay;

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

                await _options.DelayAsync(delay, ct).ConfigureAwait(false);
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
        if (!string.IsNullOrEmpty(_settings.ApiKey))
            client.DefaultRequestHeaders.Add("X-API-KEY", _settings.ApiKey);
        else
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BearerToken);

        var machineName = string.IsNullOrEmpty(_settings.MachineName)
            ? $"tentacle-{subscriptionId[..Math.Min(8, subscriptionId.Length)]}"
            : _settings.MachineName;

        var payload = new Dictionary<string, object>
        {
            ["machineName"] = machineName,
            ["thumbprint"] = thumbprint,
            ["subscriptionId"] = subscriptionId,
            ["spaceId"] = _settings.SpaceId,
            ["roles"] = _settings.Roles ?? string.Empty,
            ["environments"] = _settings.Environments ?? string.Empty,
            ["agentVersion"] = _settings.AgentVersion,
            ["releaseName"] = _settings.ReleaseName,
            ["helmNamespace"] = _settings.HelmNamespace,
            ["chartRef"] = _settings.ChartRef
        };

        foreach (var kv in _extraProperties)
            payload[kv.Key] = kv.Value;

        var response = await client.PostAsJsonAsync(_registrationPath, payload, JsonOptions, ct).ConfigureAwait(false);
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

public sealed class TentacleRegistrationClientOptions
{
    public int MaxRetries { get; init; } = 10;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } =
        static (delay, ct) => Task.Delay(delay, ct);

    public static TentacleRegistrationClientOptions Default { get; } = new();
}

public class RegistrationResult
{
    public int MachineId { get; set; }
    public string ServerThumbprint { get; set; } = string.Empty;
    public string SubscriptionUri { get; set; } = string.Empty;
}
