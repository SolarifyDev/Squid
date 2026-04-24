using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Squid.Message.Hardening;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Configuration;
using Serilog;

namespace Squid.Tentacle.Flavors.LinuxTentacle;

/// <summary>
/// Registers a Listening Tentacle with the Squid Server via
/// <c>POST /api/machines/register/tentacle-listening</c>. The agent sends its
/// URI (host:port) and thumbprint so the server knows how to connect back.
///
/// <para>Follows the project-wide three-mode hardening pattern (CLAUDE.md
/// §"Hardening Three-Mode Enforcement"). When <see cref="TentacleSettings.ServerUrl"/>
/// is <c>http://</c> AND a credential (ApiKey / BearerToken) is being attached,
/// behaviour depends on the <see cref="EnforcementMode"/> resolved from
/// <see cref="EnforcementEnvVar"/>: Off (silent allow), Warn (default — allow +
/// structured warning, preserves backward compat), Strict (reject + throw).</para>
/// </summary>
public sealed class TentacleListeningRegistrar : ITentacleRegistrar
{
    /// <summary>
    /// Env var that selects enforcement mode for the http+secret cleartext
    /// guard. Recognised values: <c>off</c> / <c>warn</c> / <c>strict</c>;
    /// default (unset / blank) is <see cref="EnforcementMode.Warn"/>.
    ///
    /// <para>Pinned literal — renaming breaks every operator who set the env
    /// var by its documented name.</para>
    /// </summary>
    public const string EnforcementEnvVar = "SQUID_REGISTER_HTTPS_ENFORCEMENT";

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
    ///
    /// <para><b>Always-throw cases (mode-independent)</b>:</para>
    /// <list type="bullet">
    ///   <item>null / empty / whitespace ServerUrl</item>
    ///   <item>Not an absolute URL</item>
    ///   <item>Scheme other than http or https</item>
    /// </list>
    ///
    /// <para><b>Always-pass cases (mode-independent)</b>:</para>
    /// <list type="bullet">
    ///   <item><c>https://</c> with or without secret</item>
    ///   <item><c>http://</c> without secret (no confidential payload)</item>
    /// </list>
    ///
    /// <para><b>Mode-dependent case</b>: <c>http://</c> with secret →
    /// Off accepts silently; Warn (default) accepts with structured warning;
    /// Strict throws.</para>
    ///
    /// <para>Exposed <c>internal static</c> so the unit suite can exercise the
    /// full (input × mode) matrix without an HTTP round-trip.</para>
    /// </summary>
    internal static void EnsureSchemeSafeForSecret(string serverUrl, bool hasSecret, EnforcementMode mode)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new InvalidOperationException(
                $"ServerUrl is empty or whitespace. Set it to 'https://<squid-server>:7078' or similar. " +
                $"This rejection is unconditional regardless of the {EnforcementEnvVar} mode — an empty " +
                "URL can't reach any server.");

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"ServerUrl '{serverUrl}' is not a valid absolute URL. Expected format: " +
                $"'https://<host>:<port>'. Unconditional rejection regardless of {EnforcementEnvVar}.");

        var scheme = uri.Scheme.ToLowerInvariant();

        if (scheme == "https") return;

        if (scheme != "http")
            throw new InvalidOperationException(
                $"ServerUrl '{serverUrl}' uses scheme '{uri.Scheme}' — only http/https are supported. " +
                $"Unconditional rejection regardless of {EnforcementEnvVar}.");

        // http:// without a credential is permitted — no cleartext secret risk.
        if (!hasSecret) return;

        EnforceHttpWithSecret(serverUrl, mode);
    }

    private static void EnforceHttpWithSecret(string serverUrl, EnforcementMode mode)
    {
        switch (mode)
        {
            case EnforcementMode.Off:
                return;

            case EnforcementMode.Warn:
                Log.Warning(
                    "ServerUrl '{ServerUrl}' uses http:// with a credential attached — the credential " +
                    "(ApiKey or BearerToken) ships in cleartext over the wire. Backward-compat mode " +
                    "(default); set {EnvVar}=strict to refuse this combination, or fix the ServerUrl " +
                    "to use https://.",
                    serverUrl, EnforcementEnvVar);
                return;

            case EnforcementMode.Strict:
                throw new InvalidOperationException(
                    $"ServerUrl '{serverUrl}' uses http:// but registration attaches a credential " +
                    "(ApiKey or BearerToken) — the secret would ship in cleartext. Fix the ServerUrl " +
                    $"to use https://. To suppress this rejection, set {EnforcementEnvVar}=warn (allow + " +
                    $"log warning) or {EnforcementEnvVar}=off (silent).");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
    }

    public async Task<TentacleRegistration> RegisterAsync(TentacleIdentity identity, CancellationToken ct)
    {
        // P0-T.6 (2026-04-24 audit): helper centralizes the "still the default sentinel?"
        // decision so this compare can't drift from the one in RegisterCommand.
        if (TentacleSettings.IsAutoRegistrationUnconfigured(_settings.ServerUrl))
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
        EnsureSchemeSafeForSecret(_settings.ServerUrl, hasSecret, EnforcementModeReader.Read(EnforcementEnvVar));

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
