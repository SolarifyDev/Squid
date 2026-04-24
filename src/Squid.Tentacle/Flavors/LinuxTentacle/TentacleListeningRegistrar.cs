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
    /// <summary>
    /// Env-var escape hatch: when set to <c>1</c>, <c>true</c>, or <c>yes</c>
    /// (case-insensitive), allows <c>ServerUrl=http://…</c> with credentials
    /// attached. Intended for dev / internal-only deploys where the
    /// operator has decided https is overkill. Default behaviour (env var
    /// unset) is fail-closed: http:// + secret throws at registration time
    /// so cleartext-credential mis-deploys surface loudly.
    ///
    /// <para>Pinned by
    /// <c>TentacleListeningRegistrarSchemeGuardTests.AllowHttpEnvVar_ConstantNamePinned</c>.
    /// Renaming this constant would break every operator who set the env
    /// var by its documented name.</para>
    /// </summary>
    public const string AllowHttpRegisterEnvVar = "SQUID_ALLOW_HTTP_REGISTER";

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

    /// <summary>
    /// Validates that <paramref name="serverUrl"/> uses a scheme safe for
    /// transmitting the credential(s) identified by <paramref name="hasSecret"/>.
    /// <list type="bullet">
    ///   <item><c>https://</c>: always safe, regardless of secret presence.</item>
    ///   <item><c>http://</c> without secret: safe — nothing confidential in flight.</item>
    ///   <item><c>http://</c> with secret: throws unless <paramref name="allowHttpOverride"/>
    ///         is <c>true</c> (operator-supplied via
    ///         <see cref="AllowHttpRegisterEnvVar"/>).</item>
    ///   <item>Anything else (ftp, file, malformed): throws regardless of other args.</item>
    /// </list>
    /// Exposed as <c>internal static</c> so the unit test suite can exercise
    /// every decision branch without needing a full HTTP round-trip.
    /// </summary>
    internal static void EnsureSchemeSafeForSecret(string serverUrl, bool hasSecret, bool allowHttpOverride)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new InvalidOperationException(
                $"ServerUrl is empty or whitespace. Set it to 'https://<squid-server>:7078' or similar. " +
                $"For dev against an http:// server, also set {AllowHttpRegisterEnvVar}=1.");

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"ServerUrl '{serverUrl}' is not a valid absolute URL. Expected format: 'https://<host>:<port>'.");

        var scheme = uri.Scheme.ToLowerInvariant();

        if (scheme == "https") return;

        if (scheme != "http")
            throw new InvalidOperationException(
                $"ServerUrl '{serverUrl}' uses scheme '{uri.Scheme}' — only http/https are supported. " +
                $"Check for typos; expected 'https://<squid-server>:7078'.");

        // http:// without a credential is permitted — no cleartext secret risk.
        if (!hasSecret) return;

        if (allowHttpOverride)
        {
            Log.Warning(
                "ServerUrl '{ServerUrl}' uses http:// with a credential attached — cleartext over the wire. " +
                "Permitted because {EnvVar}=1 is set. Verify this is a dev/internal-only deployment.",
                serverUrl, AllowHttpRegisterEnvVar);
            return;
        }

        throw new InvalidOperationException(
            $"ServerUrl '{serverUrl}' uses http:// but registration attaches a credential (ApiKey or " +
            $"BearerToken) — the secret would ship in cleartext over the network. Fix the ServerUrl " +
            $"to use https://, OR — for dev / internal-only deploys where you accept the risk — set " +
            $"{AllowHttpRegisterEnvVar}=1 to opt in.");
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

        var hasSecret = !string.IsNullOrEmpty(_settings.ApiKey) || !string.IsNullOrEmpty(_settings.BearerToken);
        EnsureSchemeSafeForSecret(_settings.ServerUrl, hasSecret, ReadAllowHttpOverride());

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

    private static bool ReadAllowHttpOverride()
    {
        var raw = Environment.GetEnvironmentVariable(AllowHttpRegisterEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return false;

        var normalized = raw.Trim().ToLowerInvariant();

        return normalized == "1" || normalized == "true" || normalized == "yes";
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
