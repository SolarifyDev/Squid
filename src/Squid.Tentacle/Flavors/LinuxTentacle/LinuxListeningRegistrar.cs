using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Serilog;

namespace Squid.Tentacle.Flavors.LinuxTentacle;

/// <summary>
/// Registers a Listening Tentacle with the Squid Server via
/// <c>POST /api/machines/register/linux-listening</c>.
/// The Agent sends its URI (host:port) and thumbprint so the Server knows how to connect back.
/// </summary>
public sealed class LinuxListeningRegistrar : ITentacleRegistrar
{
    private readonly TentacleSettings _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public LinuxListeningRegistrar(TentacleSettings settings)
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
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri(_settings.ServerUrl);

        if (!string.IsNullOrEmpty(_settings.ApiKey))
            client.DefaultRequestHeaders.Add("X-API-KEY", _settings.ApiKey);
        else if (!string.IsNullOrEmpty(_settings.BearerToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BearerToken);

        var machineName = string.IsNullOrEmpty(_settings.MachineName)
            ? $"linux-{Dns.GetHostName()}"
            : _settings.MachineName;

        var payload = new Dictionary<string, object>
        {
            ["machineName"] = machineName,
            ["uri"] = listeningUri,
            ["thumbprint"] = identity.Thumbprint,
            ["spaceId"] = _settings.SpaceId,
            ["roles"] = ParseCsvToList(_settings.Roles),
            ["environmentIds"] = ParseEnvironmentIds(_settings.Environments),
            ["agentVersion"] = _settings.AgentVersion
        };

        var response = await client.PostAsJsonAsync("/api/machines/register/linux-listening", payload, JsonOptions, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

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
        var host = !string.IsNullOrWhiteSpace(_settings.ListeningHostName)
            ? _settings.ListeningHostName
            : Dns.GetHostName();

        var port = _settings.ListeningPort > 0 ? _settings.ListeningPort : 10933;

        return $"https://{host}:{port}/";
    }

    private static List<string> ParseCsvToList(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new List<string>();

        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static List<int> ParseEnvironmentIds(string environments)
    {
        // The server will resolve environment names to IDs — for Listening registration,
        // the command takes environmentIds (int[]), not names.
        // If the user provides numeric IDs, parse them; otherwise return empty (server will ignore).
        if (string.IsNullOrWhiteSpace(environments)) return new List<int>();

        var ids = new List<int>();

        foreach (var part in environments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var id))
                ids.Add(id);
        }

        return ids;
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
