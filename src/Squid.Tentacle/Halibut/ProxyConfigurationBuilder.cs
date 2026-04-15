using System.Net;
using global::Halibut;
using global::Halibut.Transport.Proxy;
using Squid.Tentacle.Configuration;

namespace Squid.Tentacle.Halibut;

/// <summary>
/// Converts <see cref="ProxySettings"/> into the wire-level proxy objects that
/// Halibut and <see cref="HttpClient"/> each need. Single source of truth so
/// polling, registration, and capabilities all go through the same proxy.
/// </summary>
public static class ProxyConfigurationBuilder
{
    /// <summary>
    /// Builds a Halibut <see cref="ProxyDetails"/> or returns null when no
    /// proxy is configured (Halibut then connects directly).
    /// </summary>
    public static ProxyDetails BuildHalibutProxy(ProxySettings settings)
    {
        if (settings == null || !settings.IsConfigured) return null;

        return new ProxyDetails(
            settings.Host,
            settings.Port,
            ProxyType.HTTP,
            string.IsNullOrWhiteSpace(settings.Username) ? null : settings.Username,
            string.IsNullOrWhiteSpace(settings.Password) ? null : settings.Password);
    }

    /// <summary>
    /// Builds an <see cref="IWebProxy"/> for <see cref="HttpClientHandler.Proxy"/>.
    /// Returns null when no proxy is configured, letting HttpClient fall back to
    /// its default resolver (env vars, system settings, or direct).
    /// </summary>
    public static IWebProxy BuildHttpClientProxy(ProxySettings settings)
    {
        if (settings == null || !settings.IsConfigured) return null;

        var proxy = new WebProxy($"http://{settings.Host}:{settings.Port}/");

        if (!string.IsNullOrWhiteSpace(settings.Username))
            proxy.Credentials = new NetworkCredential(settings.Username, settings.Password);

        return proxy;
    }
}
