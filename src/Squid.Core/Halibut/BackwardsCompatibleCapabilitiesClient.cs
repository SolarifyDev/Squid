using Halibut;
using Serilog;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Core.Halibut;

/// <summary>
/// Wraps <see cref="IAsyncCapabilitiesService"/> so that a newer server can
/// still talk to an older Tentacle that doesn't yet implement
/// <see cref="ICapabilitiesService"/>. When the inner call throws a Halibut
/// "no such service/method" error, this decorator silently synthesises a
/// minimum capability list — matching the fallback Octopus does in
/// BackwardsCompatibleAsyncCapabilitiesV2Decorator — so mixed-version
/// deployments don't fail the health check outright.
///
/// Any other exception is rethrown so operators still see real transport
/// errors rather than having them masked as "old agent".
/// </summary>
public sealed class BackwardsCompatibleCapabilitiesClient : IAsyncCapabilitiesService
{
    private readonly IAsyncCapabilitiesService _inner;

    public BackwardsCompatibleCapabilitiesClient(IAsyncCapabilitiesService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request)
    {
        try
        {
            return await _inner.GetCapabilitiesAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoSuchServiceError(ex))
        {
            Log.Warning(
                "Agent does not implement ICapabilitiesService (mixed-version deployment) — synthesising minimum capability set. {Message}",
                ex.Message);
            return SynthesizeMinimumCapabilities();
        }
    }

    private static CapabilitiesResponse SynthesizeMinimumCapabilities()
    {
        return new CapabilitiesResponse
        {
            SupportedServices = new List<string> { "IScriptService/v1" },
            AgentVersion = "unknown (pre-capabilities)",
            Metadata = new Dictionary<string, string>
            {
                // Don't lie about OS/shell — leave empty so the contributor falls back to defaults.
                // The rest of the pipeline treats missing metadata the same as a never-health-checked
                // machine, which is safe.
            }
        };
    }

    internal static bool IsNoSuchServiceError(Exception ex)
    {
        // Halibut's error messages for missing services/methods vary by version; match
        // on the substrings that have remained stable across Halibut 7.x/8.x:
        //   "No service found ..."
        //   "NoMatchingService..."
        //   "Method ... not found"
        //   "The service contract ... does not contain a method ..."
        if (ex is HalibutClientException hce)
        {
            var msg = hce.Message ?? string.Empty;
            if (msg.Contains("NoMatchingService", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("No service found", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("service contract", StringComparison.OrdinalIgnoreCase)
                && msg.Contains("not contain a method", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("Method", StringComparison.OrdinalIgnoreCase)
                && msg.Contains("not found", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
