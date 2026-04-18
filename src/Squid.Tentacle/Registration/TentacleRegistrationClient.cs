using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Squid.Tentacle.Certificate;
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
        using var handler = BuildHandler();

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
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        await EnsureBusinessSuccessOrThrowAsync(response, body).ConfigureAwait(false);

        var result = JsonSerializer.Deserialize<RegistrationResponse>(body, JsonOptions);

        EnsureRegistrationPayloadComplete(result, response, body);

        Log.Information("Registration successful. MachineId={MachineId}, ServerThumbprint={ServerThumbprint}",
            result.Data.MachineId, result.Data.ServerThumbprint);

        return new RegistrationResult
        {
            MachineId = result.Data.MachineId,
            ServerThumbprint = result.Data.ServerThumbprint ?? string.Empty,
            SubscriptionUri = result.Data.SubscriptionUri ?? $"poll://{subscriptionId}/"
        };
    }

    /// <summary>
    /// The Squid Server envelopes every response in <c>SquidResponse&lt;T&gt;</c>
    /// with a body-level <c>code</c> field mirroring the logical HTTP status.
    /// A <c>GlobalExceptionFilter</c> on the server wraps thrown exceptions into
    /// this envelope but always returns the transport-level HTTP status as 200
    /// (so every error surfaces as body <c>code=4xx/5xx</c> inside an HTTP 200
    /// response). Relying on <see cref="HttpResponseMessage.IsSuccessStatusCode"/>
    /// alone therefore misses business-level failures and lets the Tentacle
    /// happily proceed with empty <c>Data</c>, producing the confusing
    /// "Registration successful. MachineId=null" regression we saw on 2026-04-18.
    /// Check both HTTP status AND body <c>code</c>.
    /// </summary>
    private static async Task EnsureBusinessSuccessOrThrowAsync(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
            ThrowRegistrationFailed((int)response.StatusCode, body, response.ReasonPhrase, response.StatusCode);

        if (string.IsNullOrWhiteSpace(body))
            return; // No body to inspect — trust HTTP status.

        BusinessEnvelope envelope = null;

        try
        {
            envelope = JsonSerializer.Deserialize<BusinessEnvelope>(body, JsonOptions);
        }
        catch
        {
            // Not our envelope shape — treat as raw success body.
        }

        if (envelope == null || envelope.Code == 0) return;

        if (envelope.Code < 200 || envelope.Code >= 300)
            ThrowRegistrationFailed(envelope.Code, envelope.Msg ?? body, response.ReasonPhrase, MapCodeToHttpStatus(envelope.Code));

        await Task.CompletedTask;
    }

    /// <summary>
    /// Even after HTTP + body codes both say 2xx, the server might return an
    /// empty <c>data</c> object — historically this happened when an upstream
    /// conflict was mapped to HTTP 200 + body code=500 with no <c>data</c>
    /// field. Without this guard the Tentacle would start polling with
    /// <c>MachineId=0</c> and a blank server thumbprint, and every subsequent
    /// Halibut handshake would be rejected by the server's trust store.
    /// </summary>
    private static void EnsureRegistrationPayloadComplete(RegistrationResponse result, HttpResponseMessage response, string body)
    {
        if (result?.Data?.MachineId > 0 && !string.IsNullOrWhiteSpace(result.Data.ServerThumbprint))
            return;

        var message = $"Registration response missing machineId or serverThumbprint. HTTP {(int)response.StatusCode}, body: {body}";
        Log.Error(message);
        throw new HttpRequestException(message, null, HttpStatusCode.BadGateway);
    }

    private static void ThrowRegistrationFailed(int code, string bodyOrMessage, string reason, HttpStatusCode? httpStatus)
    {
        var message = string.IsNullOrWhiteSpace(bodyOrMessage)
            ? $"Registration failed with code {code} ({reason})"
            : $"Registration failed with code {code}: {bodyOrMessage}";

        Log.Error(message);
        throw new HttpRequestException(message, null, httpStatus);
    }

    private static HttpStatusCode MapCodeToHttpStatus(int code)
        => Enum.IsDefined(typeof(HttpStatusCode), code) ? (HttpStatusCode)code : HttpStatusCode.InternalServerError;

    private HttpMessageHandler BuildHandler()
    {
        if (_options.HttpMessageHandlerFactory != null)
            return _options.HttpMessageHandlerFactory();

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ServerCertificateValidator.Create(_settings.ServerCertificate)
        };

        var proxy = Squid.Tentacle.Halibut.ProxyConfigurationBuilder.BuildHttpClientProxy(_settings.Proxy);
        if (proxy != null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        return handler;
    }

    private class BusinessEnvelope
    {
        public int Code { get; set; }

        public string Msg { get; set; }
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

    /// <summary>
    /// Test seam: override the underlying <see cref="HttpMessageHandler"/> so
    /// unit tests can stub HTTP responses without a real listener. Production
    /// code leaves this null and the client builds a real <see cref="HttpClientHandler"/>
    /// configured with the server cert validator and any proxy settings.
    /// </summary>
    public Func<HttpMessageHandler> HttpMessageHandlerFactory { get; init; }

    public static TentacleRegistrationClientOptions Default { get; } = new();
}

public class RegistrationResult
{
    public int MachineId { get; set; }
    public string ServerThumbprint { get; set; } = string.Empty;
    public string SubscriptionUri { get; set; } = string.Empty;
}
