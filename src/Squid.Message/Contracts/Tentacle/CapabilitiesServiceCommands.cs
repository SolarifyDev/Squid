namespace Squid.Message.Contracts.Tentacle;

/// <summary>
/// CapabilitiesRequest implements <see cref="System.Collections.Generic.IEnumerable{T}"/>
/// over <c>string</c> SOLELY to satisfy Halibut 8.1's
/// <c>ParameterCacheKeys.GenerateCacheKey</c> — which only accepts
/// <c>string</c> / <c>Guid</c> / <c>DateTime</c> / <c>DateTimeOffset</c> /
/// <c>IEnumerable</c>. An empty class threw
/// <c>ArgumentOutOfRangeException</c> on every <c>[CacheResponse]</c>-
/// decorated call (the attribute on
/// <see cref="ICapabilitiesService.GetCapabilities"/> triggers cache-key
/// generation BEFORE the RPC). Pre-fix: every production health check
/// silently failed with "Tentacle connectivity failed: ...cache key..."
/// because <see cref="Squid.Core.Services.DeploymentExecution.Targets.Tentacle.Transport"/>
/// catches everything as connectivity loss.
///
/// <para><b>Wire-shape preservation</b>: <c>[Newtonsoft.Json.JsonObject]</c>
/// forces serialization as a JSON OBJECT (<c>{}</c>) rather than a JSON
/// ARRAY (<c>[]</c>). Without this attribute, Newtonsoft sees the
/// <see cref="System.Collections.Generic.IEnumerable{T}"/> implementation
/// and serializes as <c>[]</c> — wire-shape-incompatible with old
/// tentacles deserializing as <c>CapabilitiesRequest</c>. The attribute
/// keeps the wire shape identical to the pre-fix empty-class form
/// (<c>{}</c>), so mixed-version deployments stay compatible.</para>
/// </summary>
[Newtonsoft.Json.JsonObject(MemberSerialization = Newtonsoft.Json.MemberSerialization.OptIn)]
public class CapabilitiesRequest : System.Collections.Generic.IEnumerable<string>
{
    public System.Collections.Generic.IEnumerator<string> GetEnumerator()
        => System.Linq.Enumerable.Empty<string>().GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => GetEnumerator();
}

public class CapabilitiesResponse
{
    public List<string> SupportedServices { get; set; } = new();

    public string AgentVersion { get; set; } = string.Empty;

    public Dictionary<string, string> Metadata { get; set; } = new();
}
