using Squid.Message.Enums;

namespace Squid.Core.Services.DeploymentExecution;

public interface ITransportRegistry : IScopedDependency
{
    IDeploymentTransport Resolve(CommunicationStyle style);
}
