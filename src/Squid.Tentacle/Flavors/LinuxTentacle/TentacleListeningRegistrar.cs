using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Configuration;
using Serilog;

namespace Squid.Tentacle.Flavors.LinuxTentacle;

/// <summary>
/// Registers a Listening Tentacle with the Squid Server via
/// <c>POST /api/machines/register/tentacle-listening</c>.
/// The Agent sends its URI (host:port) and thumbprint so the Server knows how to connect back.
/// </summary>
public sealed class TentacleListeningRegistrar : ITentacleRegistrar
{
    private readonly TentacleSettings _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public TentacleListeningRegistrar(TentacleSettings settings)
    {
        _settings = settings;
    }

    public async Task<TentacleRegistration> RegisterAsync(TentacleIdentity identity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl) || _settings.ServerUrl == "https://localhost:7078")
        {
            Log.Warning("Listening mode — no ServerUrl configured, skipping auto-registration");

            return new TentacleRegistration
            {
                MachineId = 0,
                ServerThumbprint = _settings.ServerCertificate,
                SubscriptionUri = string.Empty
            };
        }

        var listeningUri = BuildListeningUri();

        Log.Information("Registering Listening Tentacle at {Uri} with {ServerUrl}", listeningUri, _settings.ServerUrl);

        using var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback =
            ServerCertificateValidator.Create(_settings.ServerCertificate);

        var proxy = Squid.Tentacle.Halibut.ProxyConfigurationBuilder.BuildHttpClientProxy(_settings.Proxy);
        if (proxy != null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri(_settings.ServerUrl);

        if (!string.IsNullOrEmpty(_settings.ApiKey))
            client.DefaultRequestHeaders.Add("X-API-KEY", _settings.ApiKey);
        else if (!string.IsNullOrEmpty(_settings.BearerToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BearerToken);

        var machineName = string.IsNullOrEmpty(_settings.MachineName)
            ? $"tentacle-{Dns.GetHostName()}"
            : _settings.MachineName;

        var payload = new Dictionary<string, object>
        {
            ["machineName"] = machineName,
            ["uri"] = listeningUri,
            ["thumbprint"] = identity.Thumbprint,
            ["spaceId"] = _settings.SpaceId,
            ["roles"] = _settings.Roles ?? string.Empty,
            ["environments"] = _settings.Environments ?? string.Empty,
            ["agentVersion"] = _settings.AgentVersion
        };

        var response = await client.PostAsJsonAsync("/api/machines/register/tentacle-listening", payload, JsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrowDetailedAsync(response, ct).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<RegistrationResponse>(json, JsonOptions);

        Log.Information("Listening Tentacle registered. MachineId={MachineId}, ServerThumbprint={ServerThumbprint}",
            result?.Data?.MachineId, result?.Data?.ServerThumbprint);

        return new TentacleRegistration
        {
            MachineId = result?.Data?.MachineId ?? 0,
            ServerThumbprint = result?.Data?.ServerThumbprint ?? _settings.ServerCertificate,
            SubscriptionUri = string.Empty
        };
    }

    private string BuildListeningUri()
    {
        var hasExplicitHostName = !string.IsNullOrWhiteSpace(_settings.ListeningHostName);
        var mode = PublicHostNameResolver.ParseMode(_settings.PublicHostNameConfiguration, hasExplicitHostName);
        var host = PublicHostNameResolver.Resolve(mode, _settings.ListeningHostName);

        // IPv6 literals need bracketing in URIs: https://[::1]:10933/
        if (host.Contains(':') && !host.StartsWith('['))
            host = $"[{host}]";

        var port = _settings.ListeningPort > 0 ? _settings.ListeningPort : 10933;

        Log.Information("Registering with Server as {Mode} → {Host}:{Port}", mode, host, port);

        return $"https://{host}:{port}/";
    }

    /// <summary>
    /// Like <c>EnsureSuccessStatusCode</c> but reads the response body first so the
    /// operator sees the Server's actual error message (e.g. "invalid API key",
    /// "machine name already exists") instead of a bare <c>HttpRequestException</c>.
    /// </summary>
    private static async Task EnsureSuccessOrThrowDetailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = string.Empty;

        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: if reading fails, still throw with status code alone.
        }

        var message = string.IsNullOrWhiteSpace(body)
            ? $"Registration failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase})"
            : $"Registration failed with HTTP {(int)response.StatusCode}: {body}";

        Log.Error(message);
        throw new HttpRequestException(message, null, response.StatusCode);
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
