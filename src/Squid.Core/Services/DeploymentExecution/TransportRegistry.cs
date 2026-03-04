using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution;

public class TransportRegistry : ITransportRegistry
{
    private readonly Dictionary<CommunicationStyle, IDeploymentTransport> _transports;

    public TransportRegistry(IEnumerable<IDeploymentTransport> transports)
    {
        _transports = transports.ToDictionary(t => t.CommunicationStyle);
    }

    public IDeploymentTransport Resolve(CommunicationStyle style)
        => style != CommunicationStyle.Unknown && _transports.TryGetValue(style, out var t) ? t : null;
}
