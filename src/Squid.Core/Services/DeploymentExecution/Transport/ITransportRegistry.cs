using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution.Transport;

public interface ITransportRegistry : IScopedDependency
{
    IDeploymentTransport Resolve(CommunicationStyle style);
}
