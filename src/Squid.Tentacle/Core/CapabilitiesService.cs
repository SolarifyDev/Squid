using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.Core;

public class CapabilitiesService : ICapabilitiesService
{
    private readonly Dictionary<string, string> _metadata;

    public CapabilitiesService() : this(metadata: null) { }

    public CapabilitiesService(Dictionary<string, string> metadata)
    {
        _metadata = MergeWithRuntimeCapabilities(metadata);
    }

    public CapabilitiesResponse GetCapabilities(CapabilitiesRequest request)
    {
        return new CapabilitiesResponse
        {
            SupportedServices = new List<string> { "IScriptService/v1", "ICapabilitiesService/v1" },
            AgentVersion = AssemblyVersion.Canonical,
            Metadata = new Dictionary<string, string>(_metadata)
        };
    }

    private static Dictionary<string, string> MergeWithRuntimeCapabilities(Dictionary<string, string> overrides)
    {
        var merged = RuntimeCapabilitiesInspector.Inspect();

        if (overrides == null) return merged;

        foreach (var (key, value) in overrides)
            merged[key] = value;

        return merged;
    }
}
